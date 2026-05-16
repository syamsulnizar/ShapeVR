using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Colocation manager v4 — auto-place table di depan host SETELAH client
/// selesai colocating.
///
/// FLOW:
///   HOST:
///     1. Create + Save + Share anchor (posisi target = depan host, jarak kecil)
///     2. Broadcast UUID ke client
///     3. TUNGGU sinyal dari client via ClientColocationReadyServerRpc:
///         - success=true  -> auto-place table di depan host, broadcast offset
///         - success=false -> host place table di local saja (no sync)
///
///   CLIENT:
///     1. Terima UUID -> load + localize anchor
///     2. Sukses -> ClientColocationReadyServerRpc(true)
///     3. Gagal  -> UI failed + tombol Retry/Cancel
///         - Cancel -> ContinueAnyway() -> ClientColocationReadyServerRpc(false)
///         - Retry  -> ulang dari awal
///
/// Saat host place table sukses, broadcast localOffset (relatif anchor).
/// Kedua sisi follow anchor via LateUpdate.
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
    [Tooltip("Jarak table dari host (meter). Kecil = dekat host.")]
    [SerializeField] private float spawnDistanceFromHost = 0.6f;

    [Header("Visual Events (wire MeshRenderer + Collider toggle di Inspector)")]
    [Tooltip("Dipanggil saat scene start. Wire ke MeshRenderer.enabled=false + Collider.enabled=false untuk semua piece + Board.")]
    public UnityEngine.Events.UnityEvent onHideVisuals;
    [Tooltip("Dipanggil setelah table placed. Wire ke MeshRenderer.enabled=true + Collider.enabled=true untuk semua piece + Board.")]
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
        // Jangan SetActive(false) WorldRoot! Hide MeshRenderer + Collider saja
        // supaya lifecycle ISDK component identik dengan scene lain.
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

        // Posisi target table = depan host, jarak kecil, di lantai
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

        // Anchor di-create di posisi target, rotation identity (hindari compound rotate)
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
        // Host TIDAK place table sekarang. Tunggu ClientColocationReadyServerRpc.
    }

    private void FailHost(string reason)
    {
        _state = State.Failed;
        _attemptInProgress = false;
        Debug.LogWarning("[ColocationManager] HOST: FAILED — " + reason);
        if (loadingUI != null) loadingUI.ShowFailedWithButtons("Host: " + reason);
    }

    /// <summary>
    /// Client kirim hasil colocation ke host.
    /// success=true  -> host auto-place table di depan host (synced)
    /// success=false -> host place table di local saja (client cancel)
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
            // localPos relatif anchor. Anchor di-create persis di _pendingTableWorldPos
            // dengan rotation identity, jadi InverseTransformPoint = offset kecil (~0).
            Vector3 localPos = _hostAnchor != null
                ? _hostAnchor.transform.InverseTransformPoint(_pendingTableWorldPos)
                : Vector3.zero;
            ApplyPlacementClientRpc(localPos);
            ApplyPlacementLocal(localPos);
        }
        else
        {
            Debug.Log("[ColocationManager] HOST: client CANCELLED colocation. Host places table locally only.");
            // Host place table di local tanpa sync (client tidak colocated)
            Vector3 localPos = _hostAnchor != null
                ? _hostAnchor.transform.InverseTransformPoint(_pendingTableWorldPos)
                : Vector3.zero;
            ApplyPlacementLocal(localPos);
            // TIDAK broadcast ke client — client jalan sendiri via ContinueAnyway-nya
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

        // Kirim sinyal SUKSES ke host -> host auto-place table
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
        // 1 frame supaya WorldRoot.position update via LateUpdate
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
    /// Wire ke Cancel button OnClick di Inspector.
    /// Client: kasih tahu host bahwa client cancel (host place table local),
    /// lalu client place table di local sendiri tanpa colocation.
    /// </summary>
    public void ContinueAnyway()
    {
        _state = State.Skipped;
        _attemptInProgress = false;

        // Kalau client, kasih tahu host supaya host tetap bisa main (place local)
        if (!IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            ClientColocationReadyServerRpc(false);
        }

        // Place table di local (tanpa anchor sync). Pakai worldRoot posisi default
        // atau di depan kamera lokal.
        _placedSuccessfully = false; // matikan follow-anchor
        if (worldRoot != null)
        {
            worldRoot.transform.SetParent(null);
            // Posisikan di depan local head kalau ada referensi
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
