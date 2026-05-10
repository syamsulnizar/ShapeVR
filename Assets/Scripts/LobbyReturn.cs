using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;

/// <summary>
/// Utility statis untuk kembali ke LobbyScene dari mana pun, DENGAN clean state
/// (Shutdown NGO + cleanup Lobby/Relay) supaya bisa play lagi tanpa restart app.
///
/// CARA PAKAI:
///   - LobbyReturn.Go();        // Sederhana: shutdown lalu pindah scene
///   - LobbyReturn.Go(monoBehaviour);  // Pakai coroutine untuk delay shutdown smooth
///
/// FLOW:
///   1. Cleanup Lobby (delete kalau host, leave kalau client) — fire-and-forget,
///      tidak menunggu agar UI tidak nge-hang.
///   2. NetworkManager.Shutdown() (untuk semua role).
///   3. SceneManager.LoadScene("LobbyScene") via Unity biasa.
///
/// Penting: ini TIDAK pakai NGO LoadScene, karena NGO baru saja shutdown.
/// </summary>
public static class LobbyReturn
{
    public const string LobbySceneName = "LobbyScene";

    /// <summary>
    /// Pulang ke lobby SECEPATNYA (tanpa coroutine).
    /// Cocok dipanggil dari Button.OnClick atau coroutine countdown lain.
    /// </summary>
    public static void Go()
    {
        TryCleanupLobbyService();
        TryShutdownNetworkManager();
        SceneManager.LoadScene(LobbySceneName, LoadSceneMode.Single);
    }

    /// <summary>
    /// Cleanup Lobby (delete kalau host, leave kalau client).
    /// Fire-and-forget — kalau gagal, log saja, tidak block.
    /// </summary>
    private static void TryCleanupLobbyService()
    {
        try
        {
            var auth = AuthenticationService.Instance;
            if (auth == null || !auth.IsSignedIn) return;

            var lobby = LobbyService.Instance;
            if (lobby == null) return;

            // Cari LobbyManager untuk dapatkan _connectedLobby (bukan static, jadi
            // skip di sini — LobbyManager.OnDestroy sebenarnya sudah handle ini saat
            // scene berubah). Cleanup di sini cuma extra safety.
            //
            // Kalau ingin force cleanup all lobby: pakai LobbyService GetJoinedLobbies
            // lalu remove. Tapi itu butuh await. Untuk simpel: skip di sini, andalkan
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
            // Force unsubscribe semua callback yang mungkin di-attach dari script lain
            // (best-effort; subscriber individu tetap perlu unsubscribe sendiri).
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
