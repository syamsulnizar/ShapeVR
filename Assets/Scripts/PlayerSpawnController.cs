using System.Collections;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Attach script ini ke OVRCameraRig di GameScene.
///
/// Cara kerja:
///   - Saat GameScene load, script ini menunggu NGO ready
///   - Lalu menggeser OVRCameraRig ke SpawnPoint yang benar
///   - Host (clientId 0) → SpawnPointA
///   - Client (clientId 1) → SpawnPointB
///
/// SETUP:
///   1. Attach script ini ke OVRCameraRig di GameScene.
///   2. Tidak perlu assign apapun di Inspector — SpawnManager.Instance dipakai otomatis.
/// </summary>
public class PlayerSpawnController : MonoBehaviour
{
    private IEnumerator Start()
    {
        // Tunggu NetworkManager ready
        yield return new WaitUntil(() =>
            NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
        );

        // Tunggu 1 frame lagi agar SpawnManager.Instance sudah terinisialisasi
        yield return null;

        if (SpawnManager.Instance == null)
        {
            Debug.LogError("[PlayerSpawnController] SpawnManager tidak ditemukan di GameScene.");
            yield break;
        }

        ulong localClientId = NetworkManager.Singleton.LocalClientId;
        Transform spawnPoint = SpawnManager.Instance.GetSpawnPoint(localClientId);

        if (spawnPoint == null) yield break;

        transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);

        Debug.Log($"[PlayerSpawnController] OVRCameraRig dipindah ke " +
                  $"{spawnPoint.name} (clientId {localClientId}) di {spawnPoint.position}");
    }
}