using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using TMPro;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

#if META_PLATFORM_SDK_DEFINED
using Meta.XR.MultiplayerBlocks.Shared;
#endif

/// <summary>
/// Menggantikan [BuildingBlock] Auto Matchmaking.
/// Koneksi dimulai saat poke, tapi MENUNGGU platform entitlement selesai
/// sebelum StartHost/StartClient agar AvatarSpawnerNGO tidak race condition.
/// </summary>
public class LobbyManager : MonoBehaviour
{
    [Header("Scene Settings")]
    [SerializeField] private string gameSceneName = "GameScene";

    [Header("UI References")]
    [SerializeField] private TextMeshPro statusText;
    [SerializeField] private GameObject pokeButton;

    [Header("Lobby Settings")]
    [SerializeField] private int maxPlayers = 2;
    [SerializeField] private string lobbyName = "ShapeVRLobby";

    private const string JoinCodeKey = "joinCode";
    private Lobby _connectedLobby;
    private bool _pokePressed = false;
    private bool _entitlementReady = false;
    private bool _ugsReady = false;

    // ------------------------------------------------------------------
    // Start: init UGS + tunggu entitlement — TANPA connect network
    // ------------------------------------------------------------------

    private async void Start()
    {
        // 0. DEFENSIVE: kalau ada session NGO lama yang masih hidup (mis. user
        // pulang ke lobby dengan cara tidak bersih), shutdown dulu agar StartHost/
        // StartClient nanti tidak fail dengan "already listening".
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsListening || nm.IsServer || nm.IsClient))
        {
            Debug.Log("[LobbyManager] Stale NetworkManager detected on lobby load \u2014 shutting down.");
            nm.Shutdown();
            for (int i = 0; i < 30 && nm.ShutdownInProgress; i++)
                await System.Threading.Tasks.Task.Yield();
        }

        // 1. Init Unity Gaming Services (idempotent \u2014 aman dipanggil ulang setiap balik lobby)
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }
#if UNITY_EDITOR
            // Hanya clear session di editor saat memang belum signed in,
            // dan hanya kalau UnityServices baru saja initialized di sesi ini.
            if (!AuthenticationService.Instance.IsSignedIn)
                AuthenticationService.Instance.ClearSessionToken();
#endif
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            _ugsReady = true;
            Debug.Log($"[LobbyManager] UGS ready. PlayerId: {AuthenticationService.Instance.PlayerId}");
        }
        catch (Exception e)
        {
            // "already signed in" race condition \u2014 tetap anggap UGS ready.
            if (AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
            {
                _ugsReady = true;
                Debug.LogWarning($"[LobbyManager] UGS init threw '{e.Message}' but IsSignedIn=true \u2014 continuing.");
            }
            else
            {
                Debug.LogError($"[LobbyManager] UGS init failed: {e.Message}");
                SetStatus("UGS error. Please restart.");
                return;
            }
        }

        // 2. Tunggu Platform entitlement (untuk AvatarSpawnerNGO)
#if META_PLATFORM_SDK_DEFINED
        // Kalau sudah pernah Succeeded di sesi sebelumnya (lobby kedua dst.),
        // callback baru tidak fire \u2014 langsung anggap ready.
        if (Meta.XR.MultiplayerBlocks.Shared.PlatformInit.status ==
            Meta.XR.MultiplayerBlocks.Shared.BBPlatformInitStatus.Succeeded)
        {
            _entitlementReady = true;
            SetStatus("Poke the button to find a match");
            Debug.Log("[LobbyManager] PlatformInit already Succeeded \u2014 skipping callback wait.");
        }
        else
        {
            SetStatus("Checking platform...");
            PlatformInit.GetEntitlementInformation(OnEntitlementDone);
        }
#else
        _entitlementReady = true;
        SetStatus("Poke the button to find a match");
#endif
    }

#if META_PLATFORM_SDK_DEFINED
    private void OnEntitlementDone(PlatformInfo info)
    {
        _entitlementReady = true;
        Debug.Log($"[LobbyManager] Entitlement done. Entitled: {info.IsEntitled}");
        SetStatus("Poke the button to find a match");
    }
#endif

    // ------------------------------------------------------------------
    // Dipanggil PokeInteractable → When Select ()
    // ------------------------------------------------------------------

    public void OnPokePlay()
    {
        if (_pokePressed) return;

        if (!_ugsReady &&
            UnityServices.State == ServicesInitializationState.Initialized &&
            AuthenticationService.Instance != null &&
            AuthenticationService.Instance.IsSignedIn)
        {
            _ugsReady = true;
        }

        if (!_ugsReady)
        {
            SetStatus("Still initializing, please wait...");
            return;
        }

        _pokePressed = true;
        if (pokeButton != null) pokeButton.SetActive(false);

        StartCoroutine(WaitForEntitlementThenConnect());
    }

    private IEnumerator WaitForEntitlementThenConnect()
    {
#if META_PLATFORM_SDK_DEFINED
        // Kalau PlatformInit sudah Succeeded dari sesi sebelumnya (app pernah
        // masuk lobby & game lalu balik), callback GetEntitlementInformation
        // mungkin tidak fire ulang. Skip wait \u2014 kita sudah entitled.
        if (Meta.XR.MultiplayerBlocks.Shared.PlatformInit.status ==
            Meta.XR.MultiplayerBlocks.Shared.BBPlatformInitStatus.Succeeded)
        {
            _entitlementReady = true;
        }
#endif

        // Tunggu entitlement selesai (max 5 detik \u2014 dipendekan dari 10s)
        float timeout = 5f;
        float elapsed = 0f;

        while (!_entitlementReady && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (!_entitlementReady)
        {
            Debug.LogWarning("[LobbyManager] Entitlement timeout, proceeding anyway.");
        }

        SetStatus("Searching for match...");
        ConnectAsync();
    }

    // ------------------------------------------------------------------
    // Connect via Relay + Lobby
    // ------------------------------------------------------------------

    private async void ConnectAsync()
    {
        try
        {
            _connectedLobby = await TryJoinOrCreate();

            bool isHost = _connectedLobby.HostId == AuthenticationService.Instance.PlayerId;

            if (isHost)
            {
                Debug.Log("[LobbyManager] Started as HOST");
                StartCoroutine(HeartbeatLobby(_connectedLobby.Id, 15f));
                SetStatus($"Waiting for opponent... (1/{maxPlayers})");
                NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            }
            else
            {
                Debug.Log("[LobbyManager] Started as CLIENT");
                SetStatus("Joined! Waiting for game to start...");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LobbyManager] Matchmaking failed: {e.Message}");
            SetStatus("Connection failed. Try again.");
            _pokePressed = false;
            if (pokeButton != null) pokeButton.SetActive(true);
        }
    }

    // ------------------------------------------------------------------
    // Host: tunggu semua player connect
    // ------------------------------------------------------------------

    private void OnClientConnected(ulong clientId)
    {
        int connected = NetworkManager.Singleton.ConnectedClientsIds.Count;
        Debug.Log($"[LobbyManager] Client {clientId} connected. {connected}/{maxPlayers}");
        SetStatus($"Players: {connected}/{maxPlayers}");

        if (connected >= maxPlayers)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            StartCoroutine(LoadGameScene(1.5f));
        }
    }

    // ------------------------------------------------------------------
    // Relay + Lobby logic
    // ------------------------------------------------------------------

    private async Task<Lobby> TryJoinOrCreate()
    {
        try { return await JoinLobby(); }
        catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.NoOpenLobbies)
        {
            Debug.Log("[LobbyManager] No lobbies, creating new.");
            return await CreateLobby();
        }
    }

    private async Task<Lobby> JoinLobby()
    {
        var lobby = await LobbyService.Instance.QuickJoinLobbyAsync();
        var join = await RelayService.Instance.JoinAllocationAsync(lobby.Data[JoinCodeKey].Value);

        FindObjectOfType<UnityTransport>().SetClientRelayData(
            join.RelayServer.IpV4, (ushort)join.RelayServer.Port,
            join.AllocationIdBytes, join.Key,
            join.ConnectionData, join.HostConnectionData);

        NetworkManager.Singleton.StartClient();
        return lobby;
    }

    private async Task<Lobby> CreateLobby()
    {
        var alloc = await RelayService.Instance.CreateAllocationAsync(maxPlayers);
        var joinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);

        var lobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers,
            new CreateLobbyOptions
            {
                IsPrivate = false,
                Data = new Dictionary<string, DataObject>
                {
                    { JoinCodeKey, new DataObject(DataObject.VisibilityOptions.Public, joinCode) }
                }
            });

        FindObjectOfType<UnityTransport>().SetHostRelayData(
            alloc.RelayServer.IpV4, (ushort)alloc.RelayServer.Port,
            alloc.AllocationIdBytes, alloc.Key, alloc.ConnectionData);

        NetworkManager.Singleton.StartHost();
        return lobby;
    }

    private IEnumerator HeartbeatLobby(string lobbyId, float interval)
    {
        var wait = new WaitForSecondsRealtime(interval);
        while (_connectedLobby != null)
        {
            LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            yield return wait;
        }
    }

    // ------------------------------------------------------------------
    // Scene Loading
    // ------------------------------------------------------------------

    private IEnumerator LoadGameScene(float delay)
    {
        SetStatus("Match found! Loading...");
        yield return new WaitForSeconds(delay);
        NetworkManager.Singleton.SceneManager.LoadScene(
            gameSceneName,
            UnityEngine.SceneManagement.LoadSceneMode.Single);
    }

    // ------------------------------------------------------------------
    // Cleanup + Helper
    // ------------------------------------------------------------------

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;

        if (_connectedLobby == null) return;
        try
        {
            if (_connectedLobby.HostId == AuthenticationService.Instance.PlayerId)
                LobbyService.Instance.DeleteLobbyAsync(_connectedLobby.Id);
            else
                LobbyService.Instance.RemovePlayerAsync(_connectedLobby.Id, AuthenticationService.Instance.PlayerId);
        }
        catch (Exception e) { Debug.Log($"[LobbyManager] Cleanup: {e.Message}"); }
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[LobbyManager] {message}");
    }
}