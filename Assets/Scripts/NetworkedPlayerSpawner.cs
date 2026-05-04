using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Attach this to your Player Prefab (the one set in NetworkManager > Player Prefab).
/// 
/// On spawn, it asks the SpawnManager for the correct position and teleports there.
/// This works alongside Meta Avatars – it moves the OVRCameraRig root, so the avatar
/// and hands follow automatically.
/// </summary>
public class NetworkedPlayerSpawner : NetworkBehaviour
{
    [Header("Optional: Override root to move")]
    [Tooltip("Leave null to move this GameObject. Set to OVRCameraRig if your player " +
             "prefab has it as a separate root.")]
    [SerializeField] private Transform playerRoot;

    public override void OnNetworkSpawn()
    {
        // Only the server repositions players to avoid client-side prediction conflicts.
        if (!IsServer) return;

        // Wait one frame so SpawnManager has registered this clientId.
        StartCoroutine(SpawnAfterFrame());
    }

    private System.Collections.IEnumerator SpawnAfterFrame()
    {
        yield return null; // one frame delay

        if (SpawnManager.Instance == null)
        {
            Debug.LogWarning("[NetworkedPlayerSpawner] SpawnManager not found in scene.");
            yield break;
        }

        Transform target = SpawnManager.Instance.GetSpawnPoint(OwnerClientId);
        Transform root = playerRoot != null ? playerRoot : transform;

        root.SetPositionAndRotation(target.position, target.rotation);

        // Sync position to all clients via a ClientRpc
        TeleportClientRpc(target.position, target.rotation);

        Debug.Log($"[NetworkedPlayerSpawner] Player {OwnerClientId} spawned at {target.position}");
    }

    /// <summary>
    /// Tells the owning client (and all others) to update the player's visual position.
    /// Necessary because NGO spawns the prefab at Vector3.zero by default.
    /// </summary>
    [ClientRpc]
    private void TeleportClientRpc(Vector3 position, Quaternion rotation)
    {
        // On the server this is already done; avoid double-move.
        if (IsServer) return;

        Transform root = playerRoot != null ? playerRoot : transform;
        root.SetPositionAndRotation(position, rotation);
    }
}