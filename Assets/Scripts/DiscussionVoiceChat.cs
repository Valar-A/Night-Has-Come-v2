using System;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;

// UI references are saved in the scene, alongside the discussion text chat.
public sealed class DiscussionVoiceChat : MonoBehaviour
{
    public UnityEngine.UI.Button micButton;
    public UnityEngine.UI.Button speakerButton;
    public UnityEngine.UI.Image micImage;
    public UnityEngine.UI.Image speakerImage;
    public Sprite micOnIcon, micOffIcon, speakerOnIcon, speakerOffIcon;
    public TMP_Text status;
    public bool MicrophoneEnabled { get; private set; }
    public bool SpeakerEnabled { get; private set; } = true;
    public bool Connected => !string.IsNullOrEmpty(_joined);
    private string _joined, _requested;
    private bool _busy, _subscribed, _destroyed, _failed;
    private float _retryAt;
    private string _error;
    private static bool _initialized;

    private void Awake()
    {
        micButton.onClick.AddListener(ToggleMicrophone);
        speakerButton.onClick.AddListener(ToggleSpeaker);
    }

    private bool MeetingActive(out NetworkGameManager gm)
    {
        gm = NetworkGameManager.Instance;
        return gm != null && gm.ChatOpen && gm.Runner != null && gm.Runner.IsRunning;
    }

    private bool CanSpeak(NetworkGameManager gm)
    {
        var obj = gm.Runner.GetPlayerObject(gm.Runner.LocalPlayer);
        return obj != null && obj.TryGetComponent(out PlayerState player) && player.IsAlive;
    }

    private void Update()
    {
        bool active = MeetingActive(out var gm);
        string desired = active ? "discussion_" + gm.Runner.SessionInfo.Name : null;
        if (!active) _failed = false;
        if (!_busy && !_failed &&
            (desired != _requested || (desired != null && !Connected && Time.unscaledTime >= _retryAt)))
            _ = Synchronize(desired, gm);
        bool canSpeak = active && CanSpeak(gm);
        if (_initialized && VivoxService.Instance.IsLoggedIn)
        {
            bool mute = !active || !Connected || !canSpeak || !MicrophoneEnabled;
            if (mute != VivoxService.Instance.IsInputDeviceMuted)
            {
                if (mute) VivoxService.Instance.MuteInputDevice();
                else VivoxService.Instance.UnmuteInputDevice();
            }
        }
        micButton.interactable = active && canSpeak && !_busy;
        speakerButton.interactable = active;
        micImage.sprite = MicrophoneEnabled && canSpeak ? micOnIcon : micOffIcon;
        speakerImage.sprite = SpeakerEnabled ? speakerOnIcon : speakerOffIcon;
        status.text = _failed ? _error : _busy ? "Connecting voice..." : Connected ?
            (!canSpeak ? "Voice: listening only" : MicrophoneEnabled ? "Voice: mic on" : "Voice: mic muted") : "Voice disconnected";
    }

    public void ToggleMicrophone()
    {
        if (!MeetingActive(out var gm) || !CanSpeak(gm)) return;
        MicrophoneEnabled = !MicrophoneEnabled;
        // A click is also an explicit retry after a service setup/network failure.
        _failed = false;
    }

    public void ToggleSpeaker()
    {
        SpeakerEnabled = !SpeakerEnabled;
        if (!_initialized || !VivoxService.Instance.IsLoggedIn) return;
        if (SpeakerEnabled) VivoxService.Instance.UnmuteOutputDevice();
        else VivoxService.Instance.MuteOutputDevice();
    }

    private async Task Synchronize(string desired, NetworkGameManager gm)
    {
        _busy = true;
        try
        {
            if (!string.IsNullOrEmpty(_requested) && _initialized && VivoxService.Instance.IsLoggedIn &&
                VivoxService.Instance.ActiveChannels.ContainsKey(_requested))
            {
                VivoxService.Instance.MuteInputDevice();
                await VivoxService.Instance.LeaveChannelAsync(_requested);
            }
            _joined = null;
            _requested = desired;
            if (desired == null || _destroyed) return;
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                // ParrelSync clones need separate local authentication profiles.
                string profile = new string(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Application.dataPath))
                    .ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
                await UnityServices.InitializeAsync(new InitializationOptions().SetProfile(profile));
            }
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            if (!_initialized)
            {
                await VivoxService.Instance.InitializeAsync();
                _initialized = true;
            }
            if (_destroyed) return;
            Subscribe();
            if (!VivoxService.Instance.IsLoggedIn)
                await VivoxService.Instance.LoginAsync(new LoginOptions { DisplayName = "P" + gm.Runner.LocalPlayer.PlayerId });
            VivoxService.Instance.MuteInputDevice();
            if (SpeakerEnabled) VivoxService.Instance.UnmuteOutputDevice(); else VivoxService.Instance.MuteOutputDevice();
            if (_destroyed || !MeetingActive(out _) || _requested != desired) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                var permission = new TaskCompletionSource<bool>();
                var callbacks = new UnityEngine.Android.PermissionCallbacks();
                callbacks.PermissionGranted += _ => permission.TrySetResult(true);
                callbacks.PermissionDenied += _ => permission.TrySetResult(false);
                callbacks.PermissionDeniedAndDontAskAgain += _ => permission.TrySetResult(false);
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
                if (!await permission.Task) throw new InvalidOperationException("Microphone permission denied");
            }
#endif
            // ChannelJoined confirms connection; awaiting this request alone is insufficient.
            await VivoxService.Instance.JoinGroupChannelAsync(desired, ChatCapability.AudioOnly);
            _retryAt = Time.unscaledTime + 10f;
        }
        catch (Exception ex)
        {
            _failed = true;
            _error = "Voice unavailable - tap mic to retry";
            Debug.LogWarning("[DiscussionVoiceChat] " + ex.Message, this);
        }
        finally { _busy = false; }
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        VivoxService.Instance.ChannelJoined += Joined;
        VivoxService.Instance.ChannelLeft += Left;
        VivoxService.Instance.LoggedOut += LoggedOut;
        _subscribed = true;
    }
    private void Joined(string name) { if (name == _requested) _joined = name; }
    private void Left(string name) { if (name == _joined) _joined = null; }
    private void LoggedOut() { _joined = null; _requested = null; _retryAt = Time.unscaledTime + 5f; }

    private async void OnDestroy()
    {
        _destroyed = true;
        micButton.onClick.RemoveListener(ToggleMicrophone);
        speakerButton.onClick.RemoveListener(ToggleSpeaker);
        if (!_initialized) return;
        if (_subscribed)
        {
            VivoxService.Instance.ChannelJoined -= Joined;
            VivoxService.Instance.ChannelLeft -= Left;
            VivoxService.Instance.LoggedOut -= LoggedOut;
        }
        try
        {
            VivoxService.Instance.MuteInputDevice();
            if (!string.IsNullOrEmpty(_requested) && VivoxService.Instance.IsLoggedIn &&
                VivoxService.Instance.ActiveChannels.ContainsKey(_requested))
                await VivoxService.Instance.LeaveChannelAsync(_requested);
            if (VivoxService.Instance.IsLoggedIn) await VivoxService.Instance.LogoutAsync();
        }
        catch (Exception ex) { Debug.LogWarning("[DiscussionVoiceChat] Cleanup: " + ex.Message); }
    }
}
