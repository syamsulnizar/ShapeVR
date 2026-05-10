using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using Unity.Netcode;

/// <summary>
/// Manager game puzzle. Server-authoritative. Handle:
///   1. Win check (semua piece IsShaped == true).
///   2. Auto-return-to-lobby setelah menang (countdown 5 detik default).
///   3. Disconnect handling:
///       - Server detect client disconnect mid-game → broadcast countdown ke
///         remaining client.
///       - Client detect dia ter-disconnect (kemungkinan host putus) → tampilkan
///         countdown lokal.
///   4. Kalau game sudah won, disconnect TIDAK trigger UI ulang (countdown win
///      sudah jalan).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class GameManager : NetworkBehaviour
{
    public enum ReturnReason
    {
        Win,
        PlayerDisconnected,
        HostLeft
    }

    [Header("Win Detection")]
    [Tooltip("Semua slot piece bentuk. Drag 9 ShapeObject di Inspector.")]
    public ShapeObject[] shapeObjects;

    [Tooltip("UnityEvent yang dipanggil di SEMUA client saat puzzle selesai (untuk SFX kemenangan, animasi, dll).")]
    public UnityEvent Won;

    [Header("Countdown Durations")]
    [Tooltip("Detik countdown auto-return-to-lobby setelah menang.")]
    [SerializeField] private float winCountdownSeconds = 5f;
    [Tooltip("Detik countdown saat player lain disconnect mid-game.")]
    [SerializeField] private float disconnectCountdownSeconds = 3f;

    [Header("UI")]
    [Tooltip("UI Panel countdown ke lobby (di world-space Canvas, di-attach di kamera VR).")]
    [SerializeField] private ReturnToLobbyUI returnToLobbyUI;

    [Header("Reason Texts")]
    [SerializeField] private string winReasonText = "Selamat! Kembali ke lobby dalam";
    [SerializeField] private string playerDisconnectedReasonText = "Pemain lain keluar. Kembali ke lobby dalam";
    [SerializeField] private string hostLeftReasonText = "Host keluar. Kembali ke lobby dalam";

    private bool _hasWon = false;
    private bool _returningToLobby = false;

    public override void OnNetworkSpawn()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        // SERVER: subscribe disconnect agar tahu kalau client putus mid-game.
        if (IsServer)
        {
            nm.OnClientDisconnectCallback += HandleServerSideClientDisconnected;
        }

        // CLIENT (non-host): subscribe disconnect agar tahu kalau diri sendiri putus
        // (host kemungkinan disconnect / shutdown).
        if (IsClient && !IsServer)
        {
            nm.OnClientDisconnectCallback += HandleLocalClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (IsServer)
            nm.OnClientDisconnectCallback -= HandleServerSideClientDisconnected;

        if (IsClient && !IsServer)
            nm.OnClientDisconnectCallback -= HandleLocalClientDisconnected;
    }

    // ============================================================
    // WIN CHECK
    // ============================================================

    /// <summary>
    /// Hanya server. Dipanggil oleh ShapeObject saat IsShaped berubah jadi true.
    /// </summary>
    public void CheckCondition()
    {
        if (!IsServer || _hasWon || _returningToLobby) return;
        if (shapeObjects == null || shapeObjects.Length == 0) return;

        if (shapeObjects.All(shape => shape != null && shape.IsShaped.Value))
        {
            _hasWon = true;
            _returningToLobby = true;
            WonClientRpc();
            StartCountdownClientRpc(winCountdownSeconds, ReturnReason.Win);
            // Server schedule actual scene load
            Invoke(nameof(ServerLoadLobby), winCountdownSeconds);
        }
    }

    [ClientRpc]
    private void WonClientRpc()
    {
        Won?.Invoke();
    }

    public void ResetWonState()
    {
        if (!IsServer) return;
        _hasWon = false;
        _returningToLobby = false;
    }

    // ============================================================
    // DISCONNECT HANDLING (server side)
    // ============================================================

    private void HandleServerSideClientDisconnected(ulong clientId)
    {
        // Abaikan kalau yang putus adalah server sendiri (clientId == ServerClientId)
        if (clientId == NetworkManager.ServerClientId) return;

        // Kalau game sudah won (atau sudah dalam proses pulang), tidak perlu UI ulang.
        if (_returningToLobby) return;

        // Trigger countdown ke client tersisa.
        _returningToLobby = true;
        StartCountdownClientRpc(disconnectCountdownSeconds, ReturnReason.PlayerDisconnected);
        Invoke(nameof(ServerLoadLobby), disconnectCountdownSeconds);
    }

    private void ServerLoadLobby()
    {
        if (!IsServer) return;
        LobbyReturn.Go();
    }

    // ============================================================
    // CLIENT-SIDE: detect host disconnect
    // ============================================================

    private void HandleLocalClientDisconnected(ulong clientId)
    {
        // Hanya peduli kalau yang disconnect = diri sendiri (ter-kick karena host putus).
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (clientId != nm.LocalClientId) return;

        // Kalau game sudah won / sudah ada countdown jalan, biarkan saja (no extra UI).
        if (_returningToLobby || (returnToLobbyUI != null && returnToLobbyUI.IsShowing)) return;

        // Tampilkan UI host-left + countdown lokal (NGO sudah down, tidak ada server).
        _returningToLobby = true;
        if (returnToLobbyUI != null)
        {
            returnToLobbyUI.Show(disconnectCountdownSeconds, hostLeftReasonText);
        }
        else
        {
            // Fallback: kalau tidak ada UI, tunggu sebentar lalu pulang.
            Invoke(nameof(LocalLoadLobby), disconnectCountdownSeconds);
        }
    }

    private void LocalLoadLobby()
    {
        LobbyReturn.Go();
    }

    // ============================================================
    // SHARED: trigger UI countdown di semua client
    // ============================================================

    [ClientRpc]
    private void StartCountdownClientRpc(float seconds, ReturnReason reason)
    {
        if (returnToLobbyUI == null) return;

        // Kalau win-countdown sudah jalan dan ini disconnect-event, abaikan
        // (Win sudah ambil precedence). Server-side guard sebenarnya sudah
        // mencegah ini, tapi double-check di client juga aman.
        if (returnToLobbyUI.IsShowing && reason != ReturnReason.Win) return;

        string txt = reason switch
        {
            ReturnReason.Win => winReasonText,
            ReturnReason.PlayerDisconnected => playerDisconnectedReasonText,
            ReturnReason.HostLeft => hostLeftReasonText,
            _ => "Kembali ke lobby dalam"
        };

        returnToLobbyUI.Show(seconds, txt);
    }

    // ============================================================
    // PUBLIC API: panggil ini dari Button untuk pulang manual
    // ============================================================

    /// <summary>Wire ke Button.OnClick — siapa pun bisa panggil, host atau client.</summary>
    public void ReturnToLobbyManual()
    {
        LobbyReturn.Go();
    }
}
