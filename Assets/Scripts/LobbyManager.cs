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
using Unity.Services.Relay.Models;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine.Events;

#if META_PLATFORM_SDK_DEFINED
using Meta.XR.MultiplayerBlocks.Shared;
#endif

/// <summary>
/// Lobby manager with matchmaking by room type. The player presses one of
/// 4 buttons (Passthrough, Virtual Mic Off, Virtual Mic On, Virtual Choose Room).
/// Players only match with others who chose the SAME button. Max 2 players per room.
/// </summary>
public class LobbyManager : MonoBehaviour
{
    [Header("Scene Settings")]
    [SerializeField] private string passthroughScene = "Passthrough";
    [SerializeField] private string virtualMicOnScene = "Virtual Mic On";
    [SerializeField] private string virtualMicOffScene = "Virtual Mic Off";
    [SerializeField] private string virtualChooseRoomScene = "Virtual Choose Room";

    [Header("UI References")]
    [SerializeField] private TextMeshPro statusText;
    [Tooltip("Parent container untuk semua tombol room. Di-hide saat loading UGS dan saat sudah dalam antrian.")]
    [SerializeField] private GameObject roomButtonsRoot;

    [Header("Lobby Settings")]
    [SerializeField] private int maxPlayers = 2;
    [SerializeField] private string lobbyName = "ShapeVRLobby";

    public const string RoomTypePassthrough = "Passthrough";
    public const string RoomTypeVirtualMicOff = "VirtualMicOff";
    public const string RoomTypeVirtualMicOn = "VirtualMicOn";
    public const string RoomTypeVirtualChooseRoom = "VirtualChooseRoom";

    public UnityEvent onMatchFound;

    private const string LobbyDataRoomTypeKey = "roomType";
    private const string LobbyDataJoinCodeKey = "joinCode";

    private Lobby _connectedLobby;
    private bool _pokePressed = false;
    private bool _entitlementReady = false;
    private bool _ugsReady = false;
    private string _selectedRoomType;
    private string _selectedSceneName;
    private Coroutine _pollCoroutine;
    private Coroutine _heartbeatCoroutine;

    private async void Start()
    {
        SetRoomButtonsVisible(false);
        SetStatus("Loading...");

        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsListening || nm.IsServer || nm.IsClient))
        {
            Debug.Log("[LobbyManager] Stale NetworkManager detected on lobby load \u2014 shutting down.");
            nm.Shutdown();
            for (int i = 0; i < 30 && nm.ShutdownInProgress; i++)
                await Task.Yield();
        }

        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }
#if UNITY_EDITOR
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

#if META_PLATFORM_SDK_DEFINED
        if (Meta.XR.MultiplayerBlocks.Shared.PlatformInit.status ==
            Meta.XR.MultiplayerBlocks.Shared.BBPlatformInitStatus.Succeeded)
        {
            _entitlementReady = true;
            ShowRoomSelection();
        }
        else
        {
            SetStatus("Checking platform...");
            PlatformInit.GetEntitlementInformation(OnEntitlementDone);
        }
#else
        _entitlementReady = true;
        ShowRoomSelection();
#endif
    }

#if META_PLATFORM_SDK_DEFINED
    private void OnEntitlementDone(PlatformInfo info)
    {
        _entitlementReady = true;
        ShowRoomSelection();
    }
#endif

    private void ShowRoomSelection()
    {
        SetStatus("Choose a room");
        SetRoomButtonsVisible(true);
    }

    private void SetRoomButtonsVisible(bool visible)
    {
        if (roomButtonsRoot != null) roomButtonsRoot.SetActive(visible);
    }

    public void OnPokePassthrough()        => StartMatchmaking(RoomTypePassthrough,        passthroughScene);
    public void OnPokeVirtualMicOff()      => StartMatchmaking(RoomTypeVirtualMicOff,      virtualMicOffScene);
    public void OnPokeVirtualMicOn()       => StartMatchmaking(RoomTypeVirtualMicOn,       virtualMicOnScene);
    public void OnPokeVirtualChooseRoom()  => StartMatchmaking(RoomTypeVirtualChooseRoom,  virtualChooseRoomScene);

    private void StartMatchmaking(string roomType, string sceneName)
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
        _selectedRoomType = roomType;
        _selectedSceneName = sceneName;
        SetRoomButtonsVisible(false);
        StartCoroutine(WaitForEntitlementThenConnect());
    }

    private IEnumerator WaitForEntitlementThenConnect()
    {
#if META_PLATFORM_SDK_DEFINED
        if (Meta.XR.MultiplayerBlocks.Shared.PlatformInit.status ==
            Meta.XR.MultiplayerBlocks.Shared.BBPlatformInitStatus.Succeeded)
        {
            _entitlementReady = true;
        }
#endif

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

        SetStatus($"Searching for {_selectedRoomType}...");
        ConnectAsync();
    }

    private async void ConnectAsync()
    {
        try
        {
            var existingLobbyId = await TryFindLobbyAsync(_selectedRoomType);

            if (!string.IsNullOrEmpty(existingLobbyId))
            {
                Debug.Log($"[LobbyManager] Found existing lobby {existingLobbyId} for {_selectedRoomType} \u2014 joining as client.");
                await JoinExistingLobbyAsync(existingLobbyId);
            }
            else
            {
                Debug.Log($"[LobbyManager] No lobby found for {_selectedRoomType} \u2014 creating as host.");
                await CreateLobbyAsHostAsync();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LobbyManager] ConnectAsync failed: {e.Message}");
            SetStatus($"Match failed: {e.Message}");
        }
    }

    private async Task<string> TryFindLobbyAsync(string roomType)
    {
        var queryOptions = new QueryLobbiesOptions
        {
            Count = 25,
            Filters = new List<QueryFilter>
            {
                new QueryFilter(
                    field: QueryFilter.FieldOptions.S1,
                    op: QueryFilter.OpOptions.EQ,
                    value: roomType),
                new QueryFilter(
                    field: QueryFilter.FieldOptions.AvailableSlots,
                    op: QueryFilter.OpOptions.GT,
                    value: "0"),
            }
        };

        var response = await LobbyService.Instance.QueryLobbiesAsync(queryOptions);
        if (response == null || response.Results == null || response.Results.Count == 0)
            return null;

        return response.Results[0].Id;
    }


    private async Task JoinExistingLobbyAsync(string lobbyId)
    {
        _connectedLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId);
        StartLobbyPolling();
        SetStatus("Match found! Connecting...");
        onMatchFound?.Invoke();
    }

    private async Task CreateLobbyAsHostAsync()
    {
        var allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
        var joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

        var lobbyOptions = new CreateLobbyOptions
        {
            IsPrivate = false,
            Data = new Dictionary<string, DataObject>
            {
                {
                    LobbyDataRoomTypeKey,
                    new DataObject(
                        visibility: DataObject.VisibilityOptions.Public,
                        value: _selectedRoomType,
                        index: DataObject.IndexOptions.S1)
                },
                {
                    LobbyDataJoinCodeKey,
                    new DataObject(
                        visibility: DataObject.VisibilityOptions.Member,
                        value: joinCode)
                }
            }
        };

        _connectedLobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers, lobbyOptions);

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
        NetworkManager.Singleton.StartHost();

        StartLobbyHeartbeat();
        StartLobbyPolling();

        SetStatus("Waiting for another player...");
    }

    private void StartLobbyPolling()
    {
        if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
        _pollCoroutine = StartCoroutine(PollLobbyCoroutine());
    }

    private void StartLobbyHeartbeat()
    {
        if (_heartbeatCoroutine != null) StopCoroutine(_heartbeatCoroutine);
        _heartbeatCoroutine = StartCoroutine(LobbyHeartbeatCoroutine());
    }

    private IEnumerator PollLobbyCoroutine()
    {
        bool clientStarted = false;
        bool sceneLoaded = false;
        const float pollInterval = 1.1f;

        while (_connectedLobby != null)
        {
            yield return new WaitForSeconds(pollInterval);

            Task<Lobby> task = null;
            try
            {
                task = LobbyService.Instance.GetLobbyAsync(_connectedLobby.Id);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LobbyManager] GetLobbyAsync failed (will retry): {e.Message}");
                continue;
            }

            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted)
            {
                Debug.LogWarning($"[LobbyManager] GetLobbyAsync faulted: {task.Exception?.Message}");
                continue;
            }

            _connectedLobby = task.Result;
            if (_connectedLobby == null) break;

            bool isHost = _connectedLobby.HostId == AuthenticationService.Instance.PlayerId;

            if (!isHost && !clientStarted)
            {
                if (_connectedLobby.Data != null &&
                    _connectedLobby.Data.TryGetValue(LobbyDataJoinCodeKey, out var joinCodeData) &&
                    !string.IsNullOrEmpty(joinCodeData.Value))
                {
                    yield return StartClientWithJoinCode(joinCodeData.Value);
                    clientStarted = true;
                }
            }

            if (isHost && !sceneLoaded && _connectedLobby.Players.Count >= maxPlayers)
            {
                yield return new WaitForSeconds(1.5f);
                if (NetworkManager.Singleton.IsServer)
                {
                    Debug.Log($"[LobbyManager] Lobby full \u2014 loading scene '{_selectedSceneName}' via NGO.");
                    NetworkManager.Singleton.SceneManager.LoadScene(_selectedSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
                    sceneLoaded = true;
                    yield break;
                }
            }
        }
    }

    private IEnumerator StartClientWithJoinCode(string joinCode)
    {
        Task<JoinAllocation> joinTask = null;
        try
        {
            joinTask = RelayService.Instance.JoinAllocationAsync(joinCode);
        }
        catch (Exception e)
        {
            Debug.LogError($"[LobbyManager] JoinAllocationAsync failed: {e.Message}");
            yield break;
        }

        while (!joinTask.IsCompleted) yield return null;
        if (joinTask.IsFaulted)
        {
            Debug.LogError($"[LobbyManager] JoinAllocation faulted: {joinTask.Exception?.Message}");
            yield break;
        }

        var joinAllocation = joinTask.Result;
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(AllocationUtils.ToRelayServerData(joinAllocation, "dtls"));
        NetworkManager.Singleton.StartClient();
        Debug.Log("[LobbyManager] StartClient called with Relay join code.");
    }

    private IEnumerator LobbyHeartbeatCoroutine()
    {
        const float heartbeatInterval = 15f;
        while (_connectedLobby != null)
        {
            yield return new WaitForSeconds(heartbeatInterval);
            if (_connectedLobby == null) break;

            Task task = null;
            try
            {
                task = LobbyService.Instance.SendHeartbeatPingAsync(_connectedLobby.Id);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LobbyManager] Heartbeat failed: {e.Message}");
                continue;
            }

            while (task != null && !task.IsCompleted) yield return null;
        }
    }

    // ------------------------------------------------------------------
    // CANCEL MATCHMAKING
    // ------------------------------------------------------------------

    /// <summary>
    /// Cancels match searching. Cleans up the lobby, shuts down NGO, and shows the
    /// room buttons again. Safe to call in any state.
    /// Wire this to the Back Button OnClick in the Inspector.
    /// </summary>
    public async void CancelMatchmaking()
    {
        Debug.Log("[LobbyManager] CancelMatchmaking called.");

        // 1. Stop polling + heartbeat
        if (_pollCoroutine != null) { StopCoroutine(_pollCoroutine); _pollCoroutine = null; }
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }

        // 2. Cleanup lobby (host: delete, client: leave). Fire-and-forget.
        if (_connectedLobby != null &&
            AuthenticationService.Instance != null &&
            AuthenticationService.Instance.IsSignedIn)
        {
            try
            {
                bool isHost = _connectedLobby.HostId == AuthenticationService.Instance.PlayerId;
                if (isHost)
                    await LobbyService.Instance.DeleteLobbyAsync(_connectedLobby.Id);
                else
                    await LobbyService.Instance.RemovePlayerAsync(_connectedLobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LobbyManager] Cancel: lobby cleanup error (non-fatal): " + e.Message);
            }
        }
        _connectedLobby = null;

        // 3. Shutdown NGO
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsListening || nm.IsServer || nm.IsClient))
        {
            nm.Shutdown();
            for (int i = 0; i < 30 && nm.ShutdownInProgress; i++)
                await System.Threading.Tasks.Task.Yield();
        }

        // 4. Reset state
        _pokePressed = false;
        _selectedRoomType = null;
        _selectedSceneName = null;

        // 5. Show room selection again
        ShowRoomSelection();
    }

        private async void OnDestroy()
    {
        if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
        if (_heartbeatCoroutine != null) StopCoroutine(_heartbeatCoroutine);

        if (_connectedLobby != null && AuthenticationService.Instance != null && AuthenticationService.Instance.IsSignedIn)
        {
            try
            {
                bool isHost = _connectedLobby.HostId == AuthenticationService.Instance.PlayerId;
                if (isHost)
                    await LobbyService.Instance.DeleteLobbyAsync(_connectedLobby.Id);
                else
                    await LobbyService.Instance.RemovePlayerAsync(_connectedLobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LobbyManager] OnDestroy lobby cleanup: {e.Message}");
            }
        }
    }

    public void ExitGame()
    {
        Application.Quit();
    }

    private void SetStatus(string s)
    {
        if (statusText != null) statusText.text = s;
        Debug.Log($"[LobbyManager] Status: {s}");
    }
}
