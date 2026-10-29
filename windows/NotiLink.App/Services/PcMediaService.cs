using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;

namespace NotiLink.App.Services;

/// <summary>Read-only + control snapshot of whatever's currently playing on THIS PC (Spotify,
/// browser, VLC, ...) — the local counterpart to Core.Models.MediaPlaybackState, which describes
/// the phone's session instead. Has no Favorite field — confirmed there's genuinely no
/// cross-app "like/favorite" concept anywhere in Windows' System Media Transport Controls
/// surface (checked the full GlobalSystemMediaTransportControlsSession API surface, not just
/// assumed). Shuffle IS real and included below (TryChangeShuffleActiveAsync/IsShuffleEnabled) —
/// there's just no OS-level distinction between "shuffle" and Spotify's own app-only "smart
/// shuffle" concept, so only plain on/off is exposed here, same as every other third-party app
/// controlling this session would see.</summary>
public sealed class PcMediaState
{
    public required string AppLabel { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public bool IsPlaying { get; init; }
    public long Position { get; init; } // ms, snapshot at the moment this state was built
    public long Duration { get; init; } // ms
    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanSeek { get; init; }
    public bool CanShuffle { get; init; }
    public bool IsShuffleOn { get; init; }
    public byte[]? AlbumArtBytes { get; init; }
}

/// <summary>Wraps Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager — the
/// same WinRT API that powers Windows' own native media flyout (Win+... media overlay / the
/// media widget in Quick Settings). "Current session" is whatever Windows itself currently
/// considers the active one (usually whichever app most recently started/changed playback),
/// exactly matching what that native flyout would show — this app doesn't pick a specific app to
/// watch, it just mirrors the OS's own notion of "what's playing right now".</summary>
internal static class PcMediaService
{
    /// <summary>Fires whenever the current session, its metadata, playback state, or timeline
    /// changes — null means nothing is currently playing/available.</summary>
    public static event Action<PcMediaState?>? StateChanged;

    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static GlobalSystemMediaTransportControlsSession? _session;
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _ = StartInternalAsync();
    }

    private static async Task StartInternalAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachToCurrentSession();
        }
        catch (Exception)
        {
            // Not available on this OS build, or the WinRT call itself failed — the toggle on
            // the now-playing bar simply never gets anything to switch to; nothing else in the
            // app depends on this.
            _started = false;
        }
    }

    public static void Stop()
    {
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        DetachSession();
        _manager = null;
        _started = false;
    }

    private static void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        AttachToCurrentSession();

    private static void AttachToCurrentSession()
    {
        DetachSession();
        _session = _manager?.GetCurrentSession();
        if (_session == null)
        {
            StateChanged?.Invoke(null);
            return;
        }
        _session.MediaPropertiesChanged += OnSessionInfoChanged;
        _session.PlaybackInfoChanged += OnSessionInfoChanged;
        _session.TimelinePropertiesChanged += OnSessionInfoChanged;
        _ = RefreshAsync(_session);
    }

    private static void DetachSession()
    {
        var session = _session;
        if (session == null) return;
        session.MediaPropertiesChanged -= OnSessionInfoChanged;
        session.PlaybackInfoChanged -= OnSessionInfoChanged;
        session.TimelinePropertiesChanged -= OnSessionInfoChanged;
        _session = null;
    }

    private static void OnSessionInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ = RefreshAsync(sender);

    private static async Task RefreshAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            byte[]? art = null;
            if (props.Thumbnail != null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms);
                art = ms.ToArray();
            }

            var controls = playback.Controls;
            var duration = timeline.EndTime - timeline.StartTime;
            var state = new PcMediaState
            {
                AppLabel = FriendlyAppName(session.SourceAppUserModelId),
                Title = props.Title ?? "",
                Artist = props.Artist ?? "",
                Album = props.AlbumTitle ?? "",
                IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Position = (long)timeline.Position.TotalMilliseconds,
                Duration = (long)duration.TotalMilliseconds,
                CanPlayPause = controls.IsPlayEnabled || controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled,
                CanNext = controls.IsNextEnabled,
                CanPrevious = controls.IsPreviousEnabled,
                CanSeek = controls.IsPlaybackPositionEnabled,
                CanShuffle = controls.IsShuffleEnabled,
                IsShuffleOn = playback.IsShuffleActive == true,
                AlbumArtBytes = art,
            };
            // The session could have changed (or been detached) while the awaits above were in
            // flight — a stale response for a session that's no longer current must never
            // overwrite the bar with the wrong track.
            if (session != _session) return;
            StateChanged?.Invoke(state);
        }
        catch (Exception)
        {
            // Best-effort — a transient failure just leaves the bar showing its last known state
            // until the next change event fires a fresh attempt.
        }
    }

    /// <summary>SourceAppUserModelId is either a plain exe name ("Spotify.exe") for Win32 apps or
    /// a "PackageFamilyName!AppId" string for packaged/UWP apps — neither is meant for display,
    /// this just strips the parts a user would never want to see.</summary>
    private static string FriendlyAppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "PC";
        var name = aumid;
        var bangIndex = name.IndexOf('!');
        if (bangIndex >= 0) name = name[..bangIndex];
        var underscoreIndex = name.IndexOf('_');
        if (underscoreIndex > 0) name = name[..underscoreIndex]; // package family name's publisher-hash suffix
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    public static void PlayPause() => _ = TryAsync(s => s.TryTogglePlayPauseAsync());
    public static void Next() => _ = TryAsync(s => s.TrySkipNextAsync());
    public static void Previous() => _ = TryAsync(s => s.TrySkipPreviousAsync());
    public static void Seek(long positionMs) => _ = TryAsync(s => s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(positionMs).Ticks));

    /// <summary>No "set shuffle to X" concept on the phone side either — same flip-the-current-
    /// state pattern as MediaController.TransportControls' own shuffle toggle there. Reads
    /// GetPlaybackInfo() fresh (a cheap local WinRT call, not a round-trip) rather than trusting
    /// whatever the last-pushed PcMediaState said, since that can be a beat stale.</summary>
    public static void ToggleShuffle()
    {
        var session = _session;
        if (session == null) return;
        var current = session.GetPlaybackInfo().IsShuffleActive == true;
        _ = TryAsync(s => s.TryChangeShuffleActiveAsync(!current));
    }

    private static async Task TryAsync(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> call)
    {
        var session = _session;
        if (session == null) return;
        try { await call(session); }
        catch (Exception) { /* the source app's own session can reject/ignore a control at any time */ }
    }
}
