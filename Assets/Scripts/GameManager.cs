using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using Unity.Netcode;

/// <summary>
/// Puzzle game manager. Server-authoritative. Handles:
///   1. Win check (all pieces have IsShaped == true).
///   2. Auto-return to lobby after winning (5-second countdown by default).
///   3. Disconnect handling:
///       - Server detects a client disconnecting mid-game -> broadcasts a countdown
///         to the remaining client.
///       - Client detects that it has been disconnected (likely because the host
///         disconnected) -> shows a local countdown.
///   4. If the game has already been won, disconnects do NOT trigger the UI again
///      (the win countdown is already running).
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
    [Tooltip("All shape piece slots. Drag 9 ShapeObject in the Inspector.")]
    public ShapeObject[] shapeObjects;

    [Tooltip("UnityEvent called on ALL clients when the puzzle is completed (for victory SFX, animation, etc.).")]
    public UnityEvent Won;

    [Header("Countdown Durations")]
    [Tooltip("Countdown seconds to auto-return to lobby after winning.")]
    [SerializeField] private float winCountdownSeconds = 5f;
    [Tooltip("Countdown seconds when another player disconnects mid-game.")]
    [SerializeField] private float disconnectCountdownSeconds = 3f;

    [Header("UI")]
    [Tooltip("Lobby countdown UI Panel (in world-space Canvas, attached to VR camera).")]
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

        // SERVER: subscribe disconnect so we know if client disconnect in the middle of game
        if (IsServer)
        {
            nm.OnClientDisconnectCallback += HandleServerSideClientDisconnected;
        }

        // CLIENT (non-host): subscribe disconnect so we know if we disconnected
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
    /// Server only
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
        if (clientId == NetworkManager.ServerClientId) return;

        if (_returningToLobby) return;

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
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (clientId != nm.LocalClientId) return;

        if (_returningToLobby || (returnToLobbyUI != null && returnToLobbyUI.IsShowing)) return;

        _returningToLobby = true;
        if (returnToLobbyUI != null)
        {
            returnToLobbyUI.Show(disconnectCountdownSeconds, hostLeftReasonText);
        }
        else
        {
            Invoke(nameof(LocalLoadLobby), disconnectCountdownSeconds);
        }
    }

    private void LocalLoadLobby()
    {
        LobbyReturn.Go();
    }

    // ============================================================
    // SHARED: trigger UI countdown on every client
    // ============================================================

    [ClientRpc]
    private void StartCountdownClientRpc(float seconds, ReturnReason reason)
    {
        if (returnToLobbyUI == null) return;

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
    // PUBLIC API
    // ============================================================

    public void ReturnToLobbyManual()
    {
        LobbyReturn.Go();
    }
}
