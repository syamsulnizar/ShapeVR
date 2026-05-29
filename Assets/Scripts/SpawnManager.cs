using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Manages spawn points for each player based on join order.
///
/// SETUP IN GAMESCENE:
///   1. Create an empty GameObject named "SpawnManager" in the root hierarchy.
///   2. Attach this script. DO NOT add a NetworkObject — this is a regular MonoBehaviour.
///   3. Create two empty children: "SpawnPointA" and "SpawnPointB", and position them as needed.
///   4. Assign both of them in the Inspector.
///
/// HOW IT WORKS:
///   - Host (clientId 0) always goes to SpawnPointA.
///   - The first client to join (clientId 1) goes to SpawnPointB.
///   - Called by NetworkedPlayerSpawner when the player prefab spawns.
/// </summary>
public class SpawnManager : MonoBehaviour
{
    [Header("Spawn Points")]
    [SerializeField] private Transform spawnPointA;
    [SerializeField] private Transform spawnPointB;

    public static SpawnManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>
    /// spawn transform based on clientId.
    /// </summary>
    public Transform GetSpawnPoint(ulong clientId)
    {
        Transform point = clientId == 0 ? spawnPointA : spawnPointB;

        if (point == null)
            Debug.LogError($"[SpawnManager] SpawnPoint untuk clientId {clientId} belum di-assign di Inspector!");

        return point;
    }

    private void OnDrawGizmos()
    {
        DrawGizmo(spawnPointA, new Color(0.2f, 0.9f, 0.4f, 0.9f), "Spawn A (Host)");
        DrawGizmo(spawnPointB, new Color(0.3f, 0.5f, 1f, 0.9f), "Spawn B (Client)");
    }

    private void DrawGizmo(Transform t, Color color, string label)
    {
        if (t == null) return;
        Gizmos.color = color;
        Gizmos.DrawSphere(t.position, 0.15f);
        Gizmos.DrawWireCube(t.position, Vector3.one * 0.4f);
#if UNITY_EDITOR
        UnityEditor.Handles.Label(t.position + Vector3.up * 0.35f, label);
#endif
    }
}