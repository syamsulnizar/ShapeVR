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

    public State CurrentState => _state;
    public event Action<Transform> OnAnchorReady;
    public event Action OnTablePlaced;

    public override void OnNetworkSpawn()
    {
        if (worldRoot != null) worldRoot.SetActive(false);
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
            spawnRot = Quaternion.LookRotation(-fwd, Vector3.up);
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
        Transform anchorTr = IsServer ? _hostAnchor?.transform : _clientAnchor?.transform;
        if (anchorTr == null || worldRoot == null) return;

        worldRoot.transform.SetParent(anchorTr, worldPositionStays: false);
        worldRoot.transform.localPosition = localPos;
        worldRoot.transform.localRotation = Quaternion.identity;
        worldRoot.SetActive(true);

        _state = State.Placed;
        if (loadingUI != null) loadingUI.Hide();
        OnTablePlaced?.Invoke();
    }

    public void RetryColocation()
    {
        if (_attemptInProgress) return;
        _state = State.Idle;
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
        if (worldRoot != null)
        {
            worldRoot.transform.SetParent(null);
            worldRoot.SetActive(true);
        }
        if (loadingUI != null) loadingUI.Hide();
    }
}
