using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;
using Meta.XR.BuildingBlocks;

/// <summary>
/// Manager colocation untuk scene Passthrough.
///
/// FLOW:
///   - Host: tunggu tracking stabil -> create anchor di posisi 1.5m depan host
///     dengan y=0 (di lantai) -> share ke group UUID -> broadcast UUID ke client
///     via ClientRpc.
///   - Client: terima UUID -> LoadAndInstantiateAnchorsFromGroup(UUID).
///   - Saat anchor ter-load (host & client), reparent WorldRoot ke anchor.
///     Game muncul di posisi fisik yang sama.
///   - Kalau timeout (mis. ruangan berbeda), trigger ColocationFailed -> kembali
///     ke lobby semua client.
///
/// SETUP:
///   - WAJIB punya NetworkObject (scene-placed).
///   - Assign field di Inspector:
///       sharedAnchorCore  = SharedSpatialAnchorCore Building Block
///       anchorPrefab      = prefab simple GameObject untuk anchor
///                           (cukup empty + OVRSpatialAnchor component)
///       worldRoot         = parent GameObject yang berisi Table + PuzzleGame
///       loadingUI         = ColocationCanvasUI script
///       hostHeadTransform = OVRCameraRig/TrackingSpace/CenterEyeAnchor
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ColocationManager : NetworkBehaviour
{
    [Header("Refs")]
    [SerializeField] private SharedSpatialAnchorCore sharedAnchorCore;
    [Tooltip("Prefab anchor (empty + OVRSpatialAnchor). Buat sendiri lewat menu Building Block atau drag manual.")]
    [SerializeField] private GameObject anchorPrefab;
    [Tooltip("Parent berisi Table + PuzzleGame yang akan di-reparent ke anchor.")]
    [SerializeField] private GameObject worldRoot;
    [Tooltip("UI panel untuk status (Validating..., Failed...).")]
    [SerializeField] private ColocationCanvasUI loadingUI;
    [Tooltip("CenterEyeAnchor pemain lokal (untuk auto-spawn position di host).")]
    [SerializeField] private Transform hostHeadTransform;

    [Header("Timing")]
    [SerializeField] private float trackingStabilizeDelay = 2f;
    [SerializeField] private float colocationTimeoutSec = 15f;
    [SerializeField] private float spawnDistanceFromHost = 1.5f;
    [SerializeField] private float failureReturnLobbyDelay = 3f;

    // Group UUID untuk anchor sharing. Pakai value tetap di session ini
    // (host generate dan kirim via Rpc, atau pakai NetworkObjectId sebagai seed).
    // Untuk simpler: pakai fixed Guid berbasis lobby joincode atau hardcode.
    private System.Guid _groupUuid;
    private bool _colocationCompleted = false;
    private bool _colocationFailed = false;
    private float _elapsedSinceStart = 0f;
    private OVRSpatialAnchor _myAnchor;
    private Vector3 _originalWorldRootPos;
    private Quaternion _originalWorldRootRot;

    public override void OnNetworkSpawn()
    {
        // Save original transform supaya kalau reset bisa balik
        if (worldRoot != null)
        {
            _originalWorldRootPos = worldRoot.transform.position;
            _originalWorldRootRot = worldRoot.transform.rotation;
            // Sembunyikan dulu sampai colocation sukses
            worldRoot.SetActive(false);
        }

        if (loadingUI != null) loadingUI.ShowValidating();

        // Subscribe events di SharedSpatialAnchorCore
        if (sharedAnchorCore != null)
        {
            sharedAnchorCore.OnSharedSpatialAnchorsLoadCompleted.AddListener(OnAnchorsLoaded);
            sharedAnchorCore.OnSpatialAnchorsShareToGroupCompleted.AddListener(OnAnchorsSharedToGroup);
        }
        else
        {
            Debug.LogError("[ColocationManager] sharedAnchorCore tidak di-assign!");
        }

        if (IsServer)
        {
            // Host: generate group UUID, buat anchor, share. Broadcast UUID ke client.
            _groupUuid = System.Guid.NewGuid();
            StartCoroutine(HostFlowCoroutine());
        }
        // Client TIDAK mulai apa-apa sampai dapat UUID dari host via Rpc.

        StartCoroutine(TimeoutWatcherCoroutine());
    }

    public override void OnNetworkDespawn()
    {
        if (sharedAnchorCore != null)
        {
            sharedAnchorCore.OnSharedSpatialAnchorsLoadCompleted.RemoveListener(OnAnchorsLoaded);
            sharedAnchorCore.OnSpatialAnchorsShareToGroupCompleted.RemoveListener(OnAnchorsSharedToGroup);
        }
    }

    // -----------------------------------------------------------------
    // HOST FLOW
    // -----------------------------------------------------------------

    private IEnumerator HostFlowCoroutine()
    {
        // 1. Wait tracking stabilizes
        Debug.Log("[ColocationManager] Host: waiting for tracking to stabilize...");
        yield return new WaitForSeconds(trackingStabilizeDelay);

        // 2. Pick spawn position: di depan host, y=0 (lantai)
        Vector3 spawnPos = Vector3.zero;
        Quaternion spawnRot = Quaternion.identity;
        if (hostHeadTransform != null)
        {
            Vector3 forward = hostHeadTransform.forward;
            forward.y = 0; forward.Normalize();
            spawnPos = hostHeadTransform.position + forward * spawnDistanceFromHost;
            spawnPos.y = 0f;  // pivot table di y=0, jadi langsung di lantai
            spawnRot = Quaternion.LookRotation(-forward, Vector3.up); // table menghadap host
        }

        Debug.Log($"[ColocationManager] Host: instantiating anchor at {spawnPos} rotation {spawnRot.eulerAngles}");

        // 3. Instantiate anchor (Building Block auto-create OVRSpatialAnchor)
        sharedAnchorCore.InstantiateSpatialAnchor(anchorPrefab, spawnPos, spawnRot);
        // Anchor object akan muncul beberapa frame kemudian sebagai child di scene.
        // Saya hook ke OnAnchorCreateCompleted? Skill: SharedSpatialAnchorCore
        // tidak expose event "created" simple - kita pakai approach polling cari
        // OVRSpatialAnchor component di scene.

        // 4. Tunggu OVRSpatialAnchor object ter-spawn dan sudah Localized
        float waitElapsed = 0f;
        OVRSpatialAnchor newAnchor = null;
        while (waitElapsed < 10f && newAnchor == null)
        {
            yield return new WaitForSeconds(0.3f);
            waitElapsed += 0.3f;
            var anchors = FindObjectsByType<OVRSpatialAnchor>(FindObjectsSortMode.None);
            foreach (var a in anchors)
            {
                if (a != null && a.Created && a.Localized)
                {
                    newAnchor = a;
                    break;
                }
            }
        }

        if (newAnchor == null)
        {
            Debug.LogError("[ColocationManager] Host: anchor creation timed out / not localized.");
            yield break; // timeoutWatcher akan trigger failure
        }

        _myAnchor = newAnchor;
        Debug.Log($"[ColocationManager] Host: anchor created & localized. Uuid={_myAnchor.Uuid}");

        // 5. Share ke group UUID (Meta SDK akan handle upload ke cloud + notify peer)
        var anchorList = new List<OVRSpatialAnchor> { _myAnchor };
        Debug.Log($"[ColocationManager] Host: sharing anchor to group {_groupUuid}");
        sharedAnchorCore.ShareSpatialAnchors(anchorList, _groupUuid);

        // 6. Broadcast group UUID ke client lewat NGO ClientRpc
        BroadcastGroupUuidClientRpc(_groupUuid.ToString());
    }

    private void OnAnchorsSharedToGroup(List<OVRSpatialAnchor> anchors, OVRAnchor.ShareResult result)
    {
        Debug.Log($"[ColocationManager] OnAnchorsSharedToGroup: result={result}");
        if (result == OVRAnchor.ShareResult.Success)
        {
            // Host: parent worldRoot ke anchor, lalu show game.
            FinalizeColocationOnHost();
        }
        else
        {
            Debug.LogError($"[ColocationManager] Anchor share failed: {result}. Aborting.");
            FailColocation("Anchor share failed: " + result);
        }
    }

    private void FinalizeColocationOnHost()
    {
        if (_myAnchor == null || worldRoot == null) return;
        worldRoot.transform.SetParent(_myAnchor.transform, worldPositionStays: false);
        worldRoot.transform.localPosition = Vector3.zero;
        worldRoot.transform.localRotation = Quaternion.identity;
        worldRoot.SetActive(true);
        _colocationCompleted = true;
        if (loadingUI != null) loadingUI.Hide();
        Debug.Log("[ColocationManager] Host: colocation complete, game visible.");
    }

    // -----------------------------------------------------------------
    // CLIENT FLOW
    // -----------------------------------------------------------------

    [ClientRpc]
    private void BroadcastGroupUuidClientRpc(string uuidString)
    {
        Debug.Log($"[ColocationManager] BroadcastGroupUuidClientRpc received: {uuidString}");
        if (IsServer) return; // host sudah handle sendiri

        if (System.Guid.TryParse(uuidString, out var uuid))
        {
            _groupUuid = uuid;
            StartCoroutine(ClientLoadAnchorCoroutine());
        }
        else
        {
            Debug.LogError("[ColocationManager] Failed parse UUID: " + uuidString);
            FailColocation("Bad UUID");
        }
    }

    private IEnumerator ClientLoadAnchorCoroutine()
    {
        // Tunggu sebentar untuk tracking stabil di client juga
        yield return new WaitForSeconds(trackingStabilizeDelay);

        Debug.Log($"[ColocationManager] Client: loading anchors from group {_groupUuid}");
        sharedAnchorCore.LoadAndInstantiateAnchorsFromGroup(anchorPrefab, _groupUuid);
    }

    private void OnAnchorsLoaded(List<OVRSpatialAnchor> anchors, OVRSpatialAnchor.OperationResult result)
    {
        Debug.Log($"[ColocationManager] OnAnchorsLoaded: result={result}, count={(anchors == null ? 0 : anchors.Count)}");
        if (IsServer) return; // host handle via different path

        if (result == OVRSpatialAnchor.OperationResult.Success && anchors != null && anchors.Count > 0)
        {
            _myAnchor = anchors[0];
            FinalizeColocationOnClient();
        }
        else
        {
            Debug.LogError($"[ColocationManager] Client: failed load anchor. Result={result}");
            FailColocation("Cannot load shared anchor (not in same room?)");
        }
    }

    private void FinalizeColocationOnClient()
    {
        if (_myAnchor == null || worldRoot == null) return;
        worldRoot.transform.SetParent(_myAnchor.transform, worldPositionStays: false);
        worldRoot.transform.localPosition = Vector3.zero;
        worldRoot.transform.localRotation = Quaternion.identity;
        worldRoot.SetActive(true);
        _colocationCompleted = true;
        if (loadingUI != null) loadingUI.Hide();
        Debug.Log("[ColocationManager] Client: colocation complete, game visible.");
    }

    // -----------------------------------------------------------------
    // FAILURE / TIMEOUT
    // -----------------------------------------------------------------

    private IEnumerator TimeoutWatcherCoroutine()
    {
        _elapsedSinceStart = 0f;
        while (_elapsedSinceStart < colocationTimeoutSec && !_colocationCompleted && !_colocationFailed)
        {
            _elapsedSinceStart += Time.deltaTime;
            yield return null;
        }

        if (!_colocationCompleted && !_colocationFailed)
        {
            Debug.LogWarning("[ColocationManager] Colocation timeout.");
            FailColocation("Colocation timed out (not in same room?)");
        }
    }

    private void FailColocation(string reason)
    {
        if (_colocationFailed) return;
        _colocationFailed = true;
        Debug.LogError($"[ColocationManager] Failing colocation: {reason}");

        if (loadingUI != null) loadingUI.ShowFailed("Not in same room. Returning to lobby...");

        if (IsServer)
        {
            // Broadcast ke semua client juga
            BroadcastFailureClientRpc(reason);
            Invoke(nameof(ServerReturnToLobby), failureReturnLobbyDelay);
        }
        else
        {
            // Client: kalau dia detect failure duluan, broadcast lewat ServerRpc supaya host & client lain juga tahu
            NotifyServerFailureServerRpc(reason);
            Invoke(nameof(LocalReturnToLobby), failureReturnLobbyDelay);
        }
    }

    [ClientRpc]
    private void BroadcastFailureClientRpc(string reason)
    {
        if (IsServer) return;
        if (_colocationFailed) return;
        _colocationFailed = true;
        if (loadingUI != null) loadingUI.ShowFailed("Not in same room. Returning to lobby...");
        Invoke(nameof(LocalReturnToLobby), failureReturnLobbyDelay);
    }

    [ServerRpc(RequireOwnership = false)]
    private void NotifyServerFailureServerRpc(string reason, ServerRpcParams rpcParams = default)
    {
        if (_colocationFailed) return;
        FailColocation(reason);
    }

    private void ServerReturnToLobby()
    {
        LobbyReturn.Go();
    }

    private void LocalReturnToLobby()
    {
        LobbyReturn.Go();
    }
}
