using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Colocation manager v3 — pakai OVRSpatialAnchor langsung, dengan retry +
/// continue-anyway + HOST PLACEMENT.
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
    [SerializeField] private float spawnDistanceFromHost = 1.5f;

    private State _state = State.Idle;
    private OVRSpatialAnchor _hostAnchor;
    private OVRSpatialAnchor _clientAnchor;
    private System.Guid _groupUuid;
    private System.Guid _anchorUuid;
    private bool _attemptInProgress = false;
    private Vector3 _anchorLocalOffset;
    private bool _placedSuccessfully = false;

    [Header("Visual Events (wire MeshRenderer + Collider toggle di Inspector)")]
    [Tooltip("Dipanggil saat scene start. Wire ke MeshRenderer.enabled=false + Collider.enabled=false untuk semua piece + Board.")]
    public UnityEngine.Events.UnityEvent onHideVisuals;
    [Tooltip("Dipanggil setelah host pinch place table. Wire ke MeshRenderer.enabled=true + Collider.enabled=true untuk semua piece + Board.")]
    public UnityEngine.Events.UnityEvent onShowVisuals;



    public State CurrentState => _state;
    public event Action<Transform> OnAnchorReady;
    public event Action OnTablePlaced;

    public override void OnNetworkSpawn()
    {
        // PENTING: jangan SetActive(false) WorldRoot! Itu akan trigger
        // OnDisable di NetworkObject piece -> race condition ISDK component.
        // Hide MeshRenderer + Collider saja. Semua component (Grabbable,
        // Rigidbody, NetworkObject) tetap active = lifecycle identik scene lain.
        HideAllVisuals();
        if (loadingUI != null) loadingUI.ShowValidating();
        StartColocationAttempt();
    }

    private void StartColocationAttempt()
    {
        if (_attemptInProgress) return;
        if (IsServer) StartCoroutine(HostFlowCoroutine());
    }

    private IEnumerator HostFlowCoroutine()
    {
        _attemptInProgress = true;
        _state = State.Validating;
        Debug.Log("[ColocationManager] HOST: starting flow.");

        yield return new WaitForSeconds(trackingStabilizeDelay);

        Vector3 spawnPos = Vector3.zero;
        Quaternion spawnRot = Quaternion.identity;
        if (hostHeadTransform != null)
        {
            var fwd = hostHeadTransform.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            spawnPos = hostHeadTransform.position + fwd * spawnDistanceFromHost;
            spawnPos.y = 0f;
            spawnRot = Quaternion.identity; // FIX: identity supaya child collider tidak compound-rotate, hindari grab miss;
        }

        var anchorGo = new GameObject("HostAnchor");
        anchorGo.transform.SetPositionAndRotation(spawnPos, spawnRot);
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
        if (loadingUI != null) loadingUI.Hide();
        Debug.Log("[ColocationManager] HOST: anchor ready. Waiting for placement.");
        OnAnchorReady?.Invoke(_hostAnchor.transform);
    }

    private void FailHost(string reason)
    {
        _state = State.Failed;
        _attemptInProgress = false;
        Debug.LogWarning("[ColocationManager] HOST: FAILED — " + reason);
        if (loadingUI != null) loadingUI.ShowFailedWithButtons("Host: " + reason);
    }

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
    }

    private void FailClient(string reason)
    {
        _state = State.Failed;
        _attemptInProgress = false;
        Debug.LogWarning("[ColocationManager] CLIENT: FAILED — " + reason);
        if (loadingUI != null) loadingUI.ShowFailedWithButtons("Client: " + reason);
    }

    /// <summary>
    /// Dipanggil HostPlacementController saat host poke "Place Here".
    /// </summary>
    public void ConfirmTablePlacement(Vector3 worldPosition)
    {
        if (!IsServer) return;
        if (_hostAnchor == null) return;
        if (_state != State.AnchorReady) return;

        Vector3 localPos = _hostAnchor.transform.InverseTransformPoint(worldPosition);
        Debug.Log("[ColocationManager] HOST: confirm placement world=" + worldPosition + " local=" + localPos);
        ApplyPlacementClientRpc(localPos);
        ApplyPlacementLocal(localPos);
    }

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
            // JANGAN SetActive(true) — WorldRoot harus selalu active sejak scene start.
        }

        _state = State.Placed;
        StartCoroutine(InitializeNetworkObjectsCoroutine());
        Debug.Log("[ColocationManager] Placement done. localOffset=" + localPos);
    }

    /// <summary>
    /// Setelah placement, verify semua NetworkObject piece sudah ready & state
    /// bersih sebelum game playable. Tampilkan loading 'Initializing...'
    /// sampai semua piece bisa di-grab.
    /// </summary>
    private System.Collections.IEnumerator InitializeNetworkObjectsCoroutine()
    {
        // 1 frame supaya WorldRoot.position update propagate ke transform anchor
        yield return null;

        // Reset Rigidbody pose untuk semua piece (safety — align ke transform)
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

        // Hide UI loading
        if (loadingUI != null) loadingUI.Hide();

        // Show visuals via UnityEvent (wire di Inspector)
        ShowAllVisuals();

        OnTablePlaced?.Invoke();
        Debug.Log("[ColocationManager] Table placed, visuals shown.");
    }

    /// <summary>
    /// Hide visual + collider tanpa SetActive(false).
    /// Semua GameObject di WorldRoot TETAP active sehingga Awake/Start/OnEnable
    /// di ISDK component (Grabbable, ThrowWhenUnselected, NetworkObject) jalan
    /// normal seperti scene tanpa colocation.
    /// </summary>
    /// <summary>
    /// Dipanggil saat scene start untuk hide visual + collider piece.
    /// Wire di Inspector OnHideVisuals UnityEvent ke MeshRenderer.enabled=false
    /// dan Collider.enabled=false untuk tiap piece + Board.
    /// </summary>
    public void HideAllVisuals()
    {
        onHideVisuals?.Invoke();
    }

    /// <summary>
    /// Re-enable MeshRenderer + Collider. Dipanggil setelah initialization done.
    /// </summary>
    /// <summary>
    /// Dipanggil setelah host pinch place table done. Wire di Inspector
    /// OnShowVisuals UnityEvent ke MeshRenderer.enabled=true + Collider.enabled=true.
    /// </summary>
    public void ShowAllVisuals()
    {
        onShowVisuals?.Invoke();
    }



    /// <summary>
    /// Follow anchor: WorldRoot di scene root, tapi position di-update tiap frame
    /// supaya tracking anchor. Hindari reparent karena bikin NetworkObject piece
    /// di hierarchy corrupt state.
    /// </summary>
    private void LateUpdate()
    {
        if (!_placedSuccessfully) return;
        if (worldRoot == null) return;

        Transform anchorTr = IsServer ? _hostAnchor?.transform : _clientAnchor?.transform;
        if (anchorTr == null) return;

        // Convert anchor-local offset ke world pose
        worldRoot.transform.position = anchorTr.TransformPoint(_anchorLocalOffset);
        worldRoot.transform.rotation = anchorTr.rotation;
    }

    public void RetryColocation()
    {
        if (_attemptInProgress) return;
        _state = State.Idle;
        _placedSuccessfully = false;
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
        if (_hostAnchor != null) { Destroy(_hostAnchor.gameObject); _hostAnchor = null; }
        StartCoroutine(HostFlowCoroutine());
    }

    public void ContinueAnyway()
    {
        _state = State.Skipped;
        _attemptInProgress = false;
        _placedSuccessfully = false;
        if (worldRoot != null)
        {
            worldRoot.transform.SetParent(null);
        }
        ShowAllVisuals();
        if (loadingUI != null) loadingUI.Hide();
    }
}
