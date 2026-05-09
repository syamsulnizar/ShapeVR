using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using Unity.Netcode;

/// <summary>
/// Cek kondisi menang puzzle. Server-authoritative.
///
/// FLOW:
///   - Tiap kali ShapeObject di server berubah jadi shaped, ShapeObject memanggil
///     GameManager.CheckCondition() — TAPI hanya pada path server (lihat
///     ShapeObject.HandleShapedChanged).
///   - Kalau semua slot shaped, server fire WonClientRpc → semua client invoke
///     UnityEvent Won (untuk play SFX, tampilkan UI, stop timer, dst).
///   - Won di-protect supaya hanya fire sekali per sesi.
///
/// SETUP:
///   - GameObject ini WAJIB punya NetworkObject component (scene-placed).
///   - Pastikan GameScene di-load lewat NGO (NetworkSceneManager.LoadScene)
///     supaya NetworkObject scene-placed otomatis ter-spawn di semua client.
///   - shapeObjects[] di-assign di Inspector (drag 9 slot bentuk).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class GameManager : NetworkBehaviour
{
    [Tooltip("UnityEvent yang dipanggil di SEMUA client saat puzzle selesai.")]
    public UnityEvent Won;

    [Tooltip("Semua slot bentuk di papan puzzle. Drag 9 ShapeObject di Inspector.")]
    public ShapeObject[] shapeObjects;

    private bool _hasWon = false;

    /// <summary>
    /// Hanya server yang boleh memanggil ini. ShapeObject akan call ini saat
    /// IsShaped berubah jadi true di sisi server.
    /// </summary>
    public void CheckCondition()
    {
        if (!IsServer || _hasWon) return;
        if (shapeObjects == null || shapeObjects.Length == 0) return;

        if (shapeObjects.All(shape => shape != null && shape.IsShaped.Value))
        {
            _hasWon = true;
            WonClientRpc();
        }
    }

    /// <summary>
    /// Reset state menang. Panggil dari host kalau mau replay tanpa reload scene.
    /// </summary>
    public void ResetWonState()
    {
        if (!IsServer) return;
        _hasWon = false;
    }

    [ClientRpc]
    private void WonClientRpc()
    {
        Won?.Invoke();
    }
}
