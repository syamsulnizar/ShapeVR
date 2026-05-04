using System.Collections;
using UnityEngine;
using TMPro;
using Unity.Netcode;

/// <summary>
/// LobbyManager — scene loading hanya terjadi ketika SEMUA player sudah poke button.
/// Menggunakan NetworkBehaviour + ServerRpc agar server bisa track siapa yang sudah poke.
///
/// PENTING: Script ini butuh NetworkObject di GameObject-nya karena pakai ServerRpc.
///          Tambahkan kembali komponen NetworkObject di Inspector.
///          Settings NetworkObject:
///            - Synchronize Transform: OFF
///            - Scene Migration Synchronization: OFF
///            - Spawn With Observers: ON
/// </summary>
public class LobbyManager : NetworkBehaviour
{
    [Header("Scene Settings")]
    [SerializeField] private string gameSceneName = "GameScene";

    [Header("UI References")]
    [SerializeField] private TextMeshPro statusText;
    [SerializeField] private GameObject pokeButton;

    [Header("Lobby Settings")]
    [SerializeField] private int maxPlayers = 2;

    // Hanya valid di server
    private int pokedCount = 0;
    private bool loadingStarted = false;

    public override void OnNetworkSpawn()
    {
    }

    // ------------------------------------------------------------------
    // Dipanggil PokeInteractable → When Select ()
    // ------------------------------------------------------------------

    public void OnPokePlay()
    {
        if (pokeButton != null) pokeButton.SetActive(false);
        SetStatus("Waiting for opponent...");

        // Kirim ke server bahwa player ini sudah poke
        ReportPokeServerRpc();
    }

    // ------------------------------------------------------------------
    // ServerRpc — diterima dan dieksekusi di server
    // ------------------------------------------------------------------

    [ServerRpc(RequireOwnership = false)]
    private void ReportPokeServerRpc(ServerRpcParams rpcParams = default)
    {
        pokedCount++;
        int connected = NetworkManager.Singleton.ConnectedClientsIds.Count;

        Debug.Log($"[LobbyManager] Poke reported. Poked: {pokedCount}, Connected: {connected}/{maxPlayers}");

        // Update status di semua client
        UpdateStatusClientRpc(pokedCount, connected);

        // Load hanya jika semua player yang connect sudah poke
        if (!loadingStarted && pokedCount >= maxPlayers && connected >= maxPlayers)
        {
            loadingStarted = true;
            StartCoroutine(LoadGameScene(1.5f));
        }
    }

    // ------------------------------------------------------------------
    // ClientRpc — update UI di semua client
    // ------------------------------------------------------------------

    [ClientRpc]
    private void UpdateStatusClientRpc(int poked, int connected)
    {
        SetStatus($"Players ready: {poked} / {maxPlayers}\nWaiting for opponent...");
    }

    // ------------------------------------------------------------------
    // Scene Loading
    // ------------------------------------------------------------------

    private IEnumerator LoadGameScene(float delay)
    {
        NotifyLoadingClientRpc();
        yield return new WaitForSeconds(delay);

        NetworkManager.Singleton.SceneManager.LoadScene(
            gameSceneName,
            UnityEngine.SceneManagement.LoadSceneMode.Single
        );
    }

    [ClientRpc]
    private void NotifyLoadingClientRpc()
    {
        SetStatus("Match found! Loading...");
    }

    // ------------------------------------------------------------------
    // Helper
    // ------------------------------------------------------------------

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[LobbyManager] {message}");
    }
}