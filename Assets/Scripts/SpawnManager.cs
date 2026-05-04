using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Mengatur spawn point untuk setiap player berdasarkan urutan join.
///
/// SETUP DI GAMESCENE:
///   1. Buat empty GameObject bernama "SpawnManager" di root hierarchy.
///   2. Attach script ini. JANGAN tambahkan NetworkObject — ini MonoBehaviour biasa.
///   3. Buat dua child empty: "SpawnPointA" dan "SpawnPointB", posisikan sesuai kebutuhan.
///   4. Assign keduanya di Inspector.
///
/// CARA KERJA:
///   - Host (clientId 0) selalu ke SpawnPointA.
///   - Client pertama yang join (clientId 1) ke SpawnPointB.
///   - Dipanggil oleh NetworkedPlayerSpawner saat player prefab spawn.
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
    /// Kembalikan spawn Transform berdasarkan clientId.
    /// clientId 0 (host) → A, sisanya → B.
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