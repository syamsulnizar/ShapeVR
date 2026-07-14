using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Vivox;

/// <summary>
/// 2D voice chat manager (group channel) using Unity Vivox.
///
/// LIFECYCLE:
///   - OnNetworkSpawn: Init Vivox -> Login -> JoinGroupChannel -> set transmit All.
///   - OnNetworkDespawn (when returning to lobby): LeaveAllChannels -> Logout.
///
/// MODE: Open mic 2D (stable volume, does not depend on avatar position).
///
/// SETUP:
///   - This GameObject MUST have a NetworkObject (scene-placed in GameScene).
///   - Vivox must be enabled in the Unity Dashboard project (Cloud -> Vivox -> Set Up).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class VoiceChatManager : NetworkBehaviour
{
    [Header("Channel")]
    [Tooltip("FIXED channel name. All peers in GameScene join this channel.")]
    [SerializeField] private string fixedChannelName = "ShapeVR-GameRoom";

    private string _channelName;
    private bool _joinedChannel = false;
    private bool _setupInProgress = false;

    public override async void OnNetworkSpawn()
    {
        _channelName = fixedChannelName;
        Debug.Log("[VoiceChatManager] OnNetworkSpawn. Channel: " + _channelName);
        await SetupAndJoinAsync();
    }

    public override async void OnNetworkDespawn()
    {
        Debug.Log("[VoiceChatManager] OnNetworkDespawn.");
        UnsubscribeEvents();
        await TearDownAsync();
    }

    private async Task SetupAndJoinAsync()
    {
        if (_setupInProgress) return;
        _setupInProgress = true;

        try
        {
            // 1. UGS init + sign-in (defensive)
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            Debug.Log("[VoiceChatManager] UGS PlayerId: " + AuthenticationService.Instance.PlayerId);

            // 2. Vivox init
            if (VivoxService.Instance.InitializationState != VivoxInitializationState.Initialized)
            {
                Debug.Log("[VoiceChatManager] Initializing Vivox...");
                await VivoxService.Instance.InitializeAsync();
            }

            // 3. Vivox login
            if (!VivoxService.Instance.IsLoggedIn)
            {
                Debug.Log("[VoiceChatManager] Vivox Login as: " + AuthenticationService.Instance.PlayerId);
                var loginOptions = new LoginOptions
                {
                    DisplayName = AuthenticationService.Instance.PlayerId,
                    EnableTTS = false
                };
                await VivoxService.Instance.LoginAsync(loginOptions);
                Debug.Log("[VoiceChatManager] Vivox login OK.");
            }

            SubscribeEvents();

            // 4. Join GROUP channel (2D, non-positional)
            Debug.Log("[VoiceChatManager] Joining group (2D) channel: " + _channelName);
            await VivoxService.Instance.JoinGroupChannelAsync(
                _channelName,
                ChatCapability.AudioOnly);

            _joinedChannel = true;
            Debug.Log("[VoiceChatManager] Joined. ActiveChannels=" + VivoxService.Instance.ActiveChannels.Count);

            // 5. Set transmission mode = All
            await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.All);
            Debug.Log("[VoiceChatManager] TransmissionMode=All. TransmittingChannels=" + VivoxService.Instance.TransmittingChannels.Count);

            // 6. Ensure mic/speaker are not muted
            if (VivoxService.Instance.IsInputDeviceMuted)
            {
                Debug.Log("[VoiceChatManager] Unmuting input device.");
                VivoxService.Instance.UnmuteInputDevice();
            }
            if (VivoxService.Instance.IsOutputDeviceMuted)
            {
                Debug.Log("[VoiceChatManager] Unmuting output device.");
                VivoxService.Instance.UnmuteOutputDevice();
            }

            var inDev = VivoxService.Instance.ActiveInputDevice;
            var outDev = VivoxService.Instance.ActiveOutputDevice;
            Debug.Log("[VoiceChatManager] InputDevice=" + (inDev != null ? inDev.DeviceName : "<null>"));
            Debug.Log("[VoiceChatManager] OutputDevice=" + (outDev != null ? outDev.DeviceName : "<null>"));
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VoiceChatManager] Setup failed: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }
        finally
        {
            _setupInProgress = false;
        }
    }

    private async Task TearDownAsync()
    {
        try
        {
            if (VivoxService.Instance == null) return;

            if (_joinedChannel)
            {
                await VivoxService.Instance.LeaveAllChannelsAsync();
                _joinedChannel = false;
            }

            if (VivoxService.Instance.IsLoggedIn)
            {
                await VivoxService.Instance.LogoutAsync();
            }
            Debug.Log("[VoiceChatManager] Teardown complete.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[VoiceChatManager] Teardown error: " + e.Message);
        }
    }

    private void SubscribeEvents()
    {
        VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemoved;
        VivoxService.Instance.ChannelJoined += OnChannelJoined;
        VivoxService.Instance.ChannelLeft += OnChannelLeft;
    }

    private void UnsubscribeEvents()
    {
        if (VivoxService.Instance == null) return;
        VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
        VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemoved;
        VivoxService.Instance.ChannelJoined -= OnChannelJoined;
        VivoxService.Instance.ChannelLeft -= OnChannelLeft;
    }

    private void OnChannelJoined(string channelName)
    {
        Debug.Log("[VoiceChatManager] >>> ChannelJoined event: " + channelName);
    }

    private void OnChannelLeft(string channelName)
    {
        Debug.Log("[VoiceChatManager] >>> ChannelLeft event: " + channelName);
    }

    private void OnParticipantAdded(VivoxParticipant participant)
    {
        Debug.Log("[VoiceChatManager] >>> ParticipantAdded: PlayerId=" + participant.PlayerId + " IsSelf=" + participant.IsSelf + " Channel=" + participant.ChannelName);
    }

    private void OnParticipantRemoved(VivoxParticipant participant)
    {
        Debug.Log("[VoiceChatManager] >>> ParticipantRemoved: PlayerId=" + participant.PlayerId + " Channel=" + participant.ChannelName);
    }
}
