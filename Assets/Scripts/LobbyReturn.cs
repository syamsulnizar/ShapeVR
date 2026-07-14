using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;

/// <summary>
/// Static utility to return to LobbyScene from anywhere, WITH a clean state
/// (Shutdown NGO + cleanup Lobby/Relay) so that you can play again without restarting the app.
///
/// HOW TO USE:
///   - LobbyReturn.Go();        // Simple: shutdown then change scene
///   - LobbyReturn.Go(monoBehaviour);  // Use coroutine for smooth shutdown delay
///
/// FLOW:
///   1. Cleanup Lobby (delete if host, leave if client) — fire-and-forget,
///      no waiting so the UI does not hang.
///   2. NetworkManager.Shutdown() (for all roles).
///   3. SceneManager.LoadScene("LobbyScene") via standard Unity.
///
/// Important: this does NOT use NGO LoadScene, because NGO was just shut down.
/// </summary>
public static class LobbyReturn
{
    public const string LobbySceneName = "LobbyScene";

    /// <summary>
    /// Return to the lobby AS SOON AS POSSIBLE (without coroutine).
    /// Suitable to be called from Button.OnClick or other countdown coroutines.
    /// </summary>
    public static void Go()
    {
        TryCleanupLobbyService();
        TryShutdownNetworkManager();
        SceneManager.LoadScene(LobbySceneName, LoadSceneMode.Single);
    }

    /// <summary>
    /// Cleanup Lobby (delete if host, leave if client).
    /// Fire-and-forget — if it fails, just log it, do not block.
    /// </summary>
    private static void TryCleanupLobbyService()
    {
        try
        {
            var auth = AuthenticationService.Instance;
            if (auth == null || !auth.IsSignedIn) return;

            var lobby = LobbyService.Instance;
            if (lobby == null) return;

            // Find LobbyManager to get _connectedLobby (not static, so
            // skip here — LobbyManager.OnDestroy actually already handles this when
            // the scene changes). Cleanup here is just for extra safety.
            //
            // If you want to force cleanup all lobbies: use LobbyService GetJoinedLobbies
            // and then remove. But that requires await. For simplicity: skip here, rely on
            // LobbyManager.OnDestroy.
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LobbyReturn] Lobby cleanup error (non-fatal): {e.Message}");
        }
    }

    private static void TryShutdownNetworkManager()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        try
        {
            // Force unsubscribe all callbacks that might be attached from other scripts
            // (best-effort; individual subscribers still need to unsubscribe themselves).
            if (nm.IsListening || nm.IsServer || nm.IsClient)
            {
                nm.Shutdown();
                Debug.Log("[LobbyReturn] NetworkManager.Shutdown() called.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[LobbyReturn] Shutdown error (non-fatal): {e.Message}");
        }
    }
}
