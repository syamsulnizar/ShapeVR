using System.Collections;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Attach this script to the OVRCameraRig in the GameScene.
///
/// How it works:
///   - When the GameScene loads, this script waits until NGO is ready.
///   - Then it moves the OVRCameraRig to the correct SpawnPoint.
///   - Host (clientId 0) -> SpawnPointA
///   - Client (clientId 1) -> SpawnPointB
///
/// SETUP:
///   1. Attach this script to the OVRCameraRig in the GameScene.
///   2. No Inspector assignment is needed — SpawnManager.Instance is used automatically.
/// </summary>
public class PlayerSpawnController : MonoBehaviour
{
    private IEnumerator Start()
    {
        // Wait NetworkManager ready
        yield return new WaitUntil(() =>
            NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
        );

        // Wait 1 frame so instance initialized
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