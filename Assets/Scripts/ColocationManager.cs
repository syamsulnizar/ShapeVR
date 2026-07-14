using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Colocation manager v4 — auto-place table in front of host AFTER client
/// finishes colocating.
///
/// FLOW:
///   HOST:
///     1. Create + Save + Share anchor (target position = in front of host, short distance)
///     2. Broadcast UUID to client
///     3. WAIT for signal from client via ClientColocationReadyServerRpc:
///         - success=true  -> auto-place table in front of host, broadcast offset
///         - success=false -> host places table locally only (no sync)
///
///   CLIENT:
///     1. Receive UUID -> load + localize anchor
///     2. Success -> ClientColocationReadyServerRpc(true)
///     3. Failed  -> UI failed + Retry/Cancel buttons
///         - Cancel -> ContinueAnyway() -> ClientColocationReadyServerRpc(false)
///         - Retry  -> start over from beginning
///
/// When host places table successfully, broadcast localOffset (relative to anchor).
/// Both sides follow anchor via LateUpdate.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ColocationManager : NetworkBehaviour
{
    public enum State { Idle, Validating, AnchorReady, Placed, Failed, Skipped }

    [Header("Refs")]
    [SerializeField] private GameObject worldRoot;
    [SerializeField] private ColocationCanvasUI loadingUI;
    [SerializeField] private Transform hostHeadTransform;

    [Header("Timing")]
    [SerializeField] private float trackingStabilizeDelay = 3f;
    [SerializeField] private float anchorCreateTimeout = 10f;
    [SerializeField] private float anchorShareTimeout = 15f;
    [SerializeField] private float clientLoadTimeout = 20f;
    [Tooltip("Distance of the table from the host (meters). Small = close to host.")]
    [SerializeField] private float spawnDistanceFromHost = 0.6f;

    [Header("Visual Events (wire MeshRenderer + Collider toggle in Inspector)")]
    [Tooltip("Called when scene starts. Wire to MeshRenderer.enabled=false + Collider.enabled=false for all pieces + Board.")]
    public UnityEngine.Events.UnityEvent onHideVisuals;
    [Tooltip("Called after table placed. Wire to MeshRenderer.enabled=true + Collider.enabled=true for all pieces + Board.")]
    public UnityEngine.Events.UnityEvent onShowVisuals;

    private State _state = State.Idle;
    private OVRSpatialAnchor _hostAnchor;
    private OVRSpatialAnchor _clientAnchor;
    private System.Guid _groupUuid;
    private System.Guid _anchorUuid;
    private bool _attemptInProgress = false;
    private Vector3 _anchorLocalOffset;
    private bool _placedSuccessfully = false;
    private Vector3 _pendingTableWorldPos;
    private bool _clientResultReceived = false;

    public State CurrentState => _state;
    public event Action<Transform> OnAnchorReady;
    public event Action OnTablePlaced;

    public override void OnNetworkSpawn()
    {
        // Do not SetActive(false) WorldRoot! Only hide MeshRenderer + Collider
        // so that the ISDK component lifecycle remains identical to other scenes.
        HideAllVisuals();
        if (loadingUI != null) loadingUI.ShowValidating();
        StartColocationAttempt();
    }

    private void StartColocationAttempt()
    {
        if (_attemptInProgress) return;
        if (IsServer) StartCoroutine(HostFlowCoroutine());
    }

    // ------------------------------------------------------------------
    // HOST FLOW
    // ------------------------------------------------------------------

    private IEnumerator HostFlowCoroutine()
    {
        _attemptInProgress = true;
        _clientResultReceived = false;
        _state = State.Validating;
        Debug.Log("[ColocationManager] HOST: starting flow.");

        yield return new WaitForSeconds(trackingStabilizeDelay);

        // Target table position = in front of host, short distance, on the floor
        Vector3 spawnPos = Vector3.zero;
        if (hostHeadTransform != null)
        {
            var fwd = hostHeadTransform.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            spawnPos = hostHeadTransform.position + fwd * spawnDistanceFromHost;
            spawnPos.y = 0f;
        }
        _pendingTableWorldPos = spawnPos;

        // Anchor is created at the target position, rotation identity (avoid compound rotation)
        var anchorGo = new GameObject("HostAnchor");
        anchorGo.transform.SetPositionAndRotation(spawnPos, Quaternion.identity);
        _hostAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();

        float waitElapsed = 0f;
        while (waitElapsed < anchorCreateTimeout &&
               (!_hostAnchor.Created || !_hostAnchor.Localized))
        {
            waitElapsed += Time.deltaTime;
            yield return null;
        }
        if (!_hostAnchor.Created || !_hostAnchor.Localized) { FailHost("Anchor not localized"); yield break; }
        _anchorUuid = _hostAnchor.Uuid;

        var saveTask = _hostAnchor.SaveAnchorAsync();
        float saveWait = 0f;
        while (!saveTask.IsCompleted && saveWait < anchorShareTimeout)
        {
            saveWait += Time.deltaTime;
            yield return null;
        }
        if (!saveTask.IsCompleted) { FailHost("Anchor save timeout"); yield break; }
        var saveResult = saveTask.GetResult();
        if (!saveResult.Success) { FailHost("Anchor save failed: " + saveResult.Status); yield break; }

        _groupUuid = System.Guid.NewGuid();
        var shareTask = _hostAnchor.ShareAsync(_groupUuid);
        float shareWait = 0f;
        while (!shareTask.IsCompleted && shareWait < anchorShareTimeout)
        {
            shareWait += Time.deltaTime;
            yield return null;
        }
        if (!shareTask.IsCompleted) { FailHost("Anchor share timeout"); yield break; }
        var shareResult = shareTask.GetResult();
        if (!shareResult.Success) { FailHost("Anchor share failed: " + shareResult.Status); yield break; }

        BroadcastAnchorClientRpc(_groupUuid.ToString(), _anchorUuid.ToString());

        _state = State.AnchorReady;
        _attemptInProgress = false;
        Debug.Log("[ColocationManager] HOST: anchor shared. Waiting for client colocation result...");
        if (loadingUI != null) loadingUI.SetCustomMessage("Waiting for player 2", "Waiting for the other player to colocate...");
        OnAnchorReady?.Invoke(_hostAnchor.transform);
        // Host does NOT place table now. Wait for ClientColocationReadyServerRpc.
    }

    private void FailHost(string reason)
    {
        _state = State.Failed;
        _attemptInProgress = false;
        Debug.LogWarning("[ColocationManager] HOST: FAILED — " + reason);
        if (loadingUI != null) loadingUI.ShowFailedWithButtons("Host: " + reason);
    }

    /// <summary>
    /// Client sends colocation result to host.
    /// success=true  -> host auto-places table in front of host (synced)
    /// success=false -> host places table locally only (client cancel)
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    private void ClientColocationReadyServerRpc(bool success, ServerRpcParams rpcParams = default)
    {
        if (_clientResultReceived) return;
        _clientResultReceived = true;

        if (_state != State.AnchorReady)
        {
            Debug.LogWarning("[ColocationManager] HOST: client result diterima tapi state bukan AnchorReady (" + _state + ")");
        }

        if (success)
        {
            Debug.Log("[ColocationManager] HOST: client colocation SUCCESS. Auto-placing table (synced).");
            // localPos relative to anchor. Anchor is created exactly at _pendingTableWorldPos
            // with rotation identity, so InverseTransformPoint = small offset (~0).
            Vector3 localPos = _hostAnchor != null
                ? _hostAnchor.transform.InverseTransformPoint(_pendingTableWorldPos)
                : Vector3.zero;
            ApplyPlacementClientRpc(localPos);
            ApplyPlacementLocal(localPos);
        }
        else
        {
            Debug.Log("[ColocationManager] HOST: client CANCELLED colocation. Host places table locally only.");
            // Host places table locally without sync (client not colocated)
            Vector3 localPos = _hostAnchor != null
                ? _hostAnchor.transform.InverseTransformPoint(_pendingTableWorldPos)
                : Vector3.zero;
            ApplyPlacementLocal(localPos);
            // DO NOT broadcast to client — client proceeds on its own via its ContinueAnyway
        }
    }

    // ------------------------------------------------------------------
    // CLIENT FLOW
    // ------------------------------------------------------------------

    [ClientRpc]
    private void BroadcastAnchorClientRpc(string groupUuidStr, string anchorUuidStr)
    {
        if (IsServer) return;
        if (!System.Guid.TryParse(groupUuidStr, out _groupUuid) ||
            !System.Guid.TryParse(anchorUuidStr, out _anchorUuid))
        {
            FailClient("Bad UUID payload");
            return;
        }
        StartCoroutine(ClientLoadCoroutine());
    }

    private IEnumerator ClientLoadCoroutine()
    {
        _attemptInProgress = true;
        _state = State.Validating;
        if (loadingUI != null) loadingUI.SetCustomMessage("Validating colocation...", "Make sure both players are in the same room.");
        yield return new WaitForSeconds(trackingStabilizeDelay);

        var unboundList = new List<OVRSpatialAnchor.UnboundAnchor>();
        var loadTask = OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(_groupUuid, unboundList);
        float loadWait = 0f;
        while (!loadTask.IsCompleted && loadWait < clientLoadTimeout)
        {
            loadWait += Time.deltaTime;
            yield return null;
        }
        if (!loadTask.IsCompleted) { FailClient("Load timeout"); yield break; }
        var loadResult = loadTask.GetResult();
        if (!loadResult.Success || unboundList.Count == 0) { FailClient("Load failed: " + loadResult.Status); yield break; }

        OVRSpatialAnchor.UnboundAnchor chosen = unboundList[0];
        for (int i = 0; i < unboundList.Count; i++)
        {
            if (unboundList[i].Uuid == _anchorUuid) { chosen = unboundList[i]; break; }
        }

        var locTask = chosen.LocalizeAsync();
        float locWait = 0f;
        while (!locTask.IsCompleted && locWait < clientLoadTimeout)
        {
            locWait += Time.deltaTime;
            yield return null;
        }
        if (!locTask.IsCompleted || !locTask.GetResult()) { FailClient("Localize failed"); yield break; }

        var anchorGo = new GameObject("ClientAnchor");
        _clientAnchor = anchorGo.AddComponent<OVRSpatialAnchor>();
        chosen.BindTo(_clientAnchor);

        _state = State.AnchorReady;
        _attemptInProgress = false;
        if (loadingUI != null) loadingUI.SetCustomMessage("Waiting for host", "Host is placing the table...");
        OnAnchorReady?.Invoke(_clientAnchor.transform);

        // Send SUCCESS signal to host -> host auto-places table
        Debug.Log("[ColocationManager] CLIENT: colocation success, notifying host.");
        ClientColocationReadyServerRpc(true);
    }

    private void FailClient(string reason)
    {
        _state = State.Failed;
        _attemptInProgress = false;
        Debug.LogWarning("[ColocationManager] CLIENT: FAILED — " + reason);
        if (loadingUI != null) loadingUI.ShowFailedWithButtons("Client: " + reason);
    }

    // ------------------------------------------------------------------
    // TABLE PLACEMENT
    // ------------------------------------------------------------------

    [ClientRpc]
    private void ApplyPlacementClientRpc(Vector3 localPos)
    {
        if (IsServer) return;
        ApplyPlacementLocal(localPos);
    }

    private void ApplyPlacementLocal(Vector3 localPos)
    {
        _anchorLocalOffset = localPos;
        _placedSuccessfully = true;

        if (worldRoot != null)
        {
            worldRoot.transform.SetParent(null, worldPositionStays: true);
        }

        _state = State.Placed;
        StartCoroutine(FinalizePlacementCoroutine());
        Debug.Log("[ColocationManager] Placement done. localOffset=" + localPos);
    }

    private IEnumerator FinalizePlacementCoroutine()
    {
        // 1 frame so that WorldRoot.position updates via LateUpdate
        yield return null;

        if (worldRoot != null)
        {
            var rbs = worldRoot.GetComponentsInChildren<Rigidbody>(includeInactive: true);
            foreach (var rb in rbs)
            {
                if (rb == null) continue;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = rb.transform.position;
                rb.rotation = rb.transform.rotation;
            }
            Physics.SyncTransforms();
        }

        if (loadingUI != null) loadingUI.Hide();
        ShowAllVisuals();
        OnTablePlaced?.Invoke();
        Debug.Log("[ColocationManager] Table placed, visuals shown.");
    }

    // ------------------------------------------------------------------
    // FOLLOW ANCHOR
    // ------------------------------------------------------------------

    private void LateUpdate()
    {
        if (!_placedSuccessfully) return;
        if (worldRoot == null) return;

        Transform anchorTr = IsServer ? _hostAnchor?.transform : _clientAnchor?.transform;
        if (anchorTr == null) return;

        worldRoot.transform.position = anchorTr.TransformPoint(_anchorLocalOffset);
        worldRoot.transform.rotation = anchorTr.rotation;
    }

    // ------------------------------------------------------------------
    // VISUAL EVENTS
    // ------------------------------------------------------------------

    public void HideAllVisuals()
    {
        onHideVisuals?.Invoke();
    }

    public void ShowAllVisuals()
    {
        onShowVisuals?.Invoke();
    }

    // ------------------------------------------------------------------
    // PUBLIC API — Inspector button wiring
    // ------------------------------------------------------------------

    public void RetryColocation()
    {
        if (_attemptInProgress) return;
        _state = State.Idle;
        _placedSuccessfully = false;
        _clientResultReceived = false;
        if (_hostAnchor != null) { Destroy(_hostAnchor.gameObject); _hostAnchor = null; }
        if (_clientAnchor != null) { Destroy(_clientAnchor.gameObject); _clientAnchor = null; }
        if (loadingUI != null) loadingUI.ShowValidating();
        if (IsServer) StartCoroutine(HostFlowCoroutine());
        else RequestRetryServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestRetryServerRpc(ServerRpcParams rpcParams = default)
    {
        if (_attemptInProgress) return;
        _clientResultReceived = false;
        if (_hostAnchor != null) { Destroy(_hostAnchor.gameObject); _hostAnchor = null; }
        StartCoroutine(HostFlowCoroutine());
    }

    /// <summary>
    /// Wire to Cancel button OnClick in Inspector.
    /// Client: notify host that client cancelled (host places table locally),
    /// then client places table locally by themselves without colocation.
    /// </summary>
    public void ContinueAnyway()
    {
        _state = State.Skipped;
        _attemptInProgress = false;

        // If client, notify host so the host can still play (place locally)
        if (!IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            ClientColocationReadyServerRpc(false);
        }

        // Place table locally (without anchor sync). Use worldRoot default position
        // or in front of the local camera.
        _placedSuccessfully = false; // disable follow-anchor
        if (worldRoot != null)
        {
            worldRoot.transform.SetParent(null);
            // Position in front of the local head if reference exists
            if (hostHeadTransform != null)
            {
                var fwd = hostHeadTransform.forward;
                fwd.y = 0;
                if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 p = hostHeadTransform.position + fwd * spawnDistanceFromHost;
                p.y = 0f;
                worldRoot.transform.position = p;
                worldRoot.transform.rotation = Quaternion.identity;
            }
        }
        ShowAllVisuals();
        if (loadingUI != null) loadingUI.Hide();
    }
}
