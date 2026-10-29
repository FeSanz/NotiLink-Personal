using System.Text.Json;
using System.Diagnostics;
using System.IO;
using NotiLink.App.Services;
using NotiLink.Core.Models;
using NotiLink.Core.Networking;
using NotiLink.Core.Storage;

namespace NotiLink.App.ViewModels;

public sealed record DeviceListEntry(string DeviceId, string Name, bool Connected, string BadgeText, string BadgeColor, bool BadgeCustom);

/// <summary>One active SIM ("línea") — dual-SIM phones report two of these independently.</summary>
public sealed record CellularLine(string CarrierName, int Level);

/// <summary>
/// Event router from TcpServer's wire messages to view-facing events. Every
/// per-phone event (notifications, battery, app list) is keyed by deviceId
/// so two paired phones never get their state mixed together — the WebView2
/// page (wwwroot/app.js) keeps a separate state bucket per device and shows
/// only the one currently selected in its device switcher. General rendering
/// preferences (font size, theme, ...) are unaffected — those already live
/// entirely in the page's own localStorage, shared across every device.
/// </summary>
public sealed class MainViewModel
{
    private readonly TcpServer _server;
    private readonly IconCache _iconCache = new();
    private readonly PairedDeviceStore _pairedDeviceStore = new();
    private readonly DiscoveryResponder _discoveryResponder = new();
    private readonly Dictionary<string, byte[]> _iconsByPackage = new();
    private readonly HashSet<string> _connectedDeviceIds = new();
    private readonly Dictionary<string, string> _deviceIps = new(); // deviceId -> remote IP (no port), for ScreenShareService
    private readonly Dictionary<string, (string DeviceName, string? DeviceModel)> _pendingPairDeviceNames = new(); // deviceId -> (deviceName, deviceModel), only while unconfirmed

    // transferId -> what it belongs to, populated when the referencing JSON
    // message is processed, consumed when the following binary frame arrives
    // (frames arrive in the fixed order documented in docs/PROTOCOL.md).
    private readonly Dictionary<string, (string DeviceId, string Key)> _pendingAvatars = new();
    private readonly Dictionary<string, (string DeviceId, string Key)> _pendingNotificationImages = new();
    private readonly Dictionary<string, (string DeviceId, string Key, string MimeType)> _pendingMessageImages = new();
    private readonly Dictionary<string, (string DeviceId, string PackageName)> _pendingIconTransfers = new();
    private readonly Dictionary<string, string> _pendingWallpaper = new(); // transferId -> deviceId
    private readonly Dictionary<string, (string DeviceId, string FileName, string MimeType)> _pendingFiles = new();
    private readonly Dictionary<string, string> _pendingMediaArt = new(); // transferId -> deviceId
    private readonly Dictionary<string, MediaPlaybackState> _lastMediaState = new(); // deviceId -> latest state
    private readonly Dictionary<string, string> _sharedFilePaths = new(); // notification key -> absolute path on this PC's disk

    public event Action<string, NotificationItem>? NotificationUpserted;      // deviceId, item
    public event Action<string, string>? NotificationRemoved;                 // deviceId, key
    /// <summary>The phone's complete current set of active notification keys, sent once per
    /// (re)connect right after the resync burst — see docs/PROTOCOL.md's notification.sync. Lets
    /// the PC clear any card it's showing for this device that the phone has no record of any
    /// more (a removal that happened while genuinely disconnected has no channel to travel over
    /// and is otherwise lost forever). JS owns the actual notification list, so it does the
    /// reconciliation itself; this just relays the raw key set up to it.</summary>
    public event Action<string, IReadOnlyList<string>>? NotificationSyncReceived;
    public event Action<string, byte[]>? IconArrived;                        // packageName, bytes — app icons are visually the same regardless of which phone reported them, so this stays global
    public event Action<string, string, byte[]>? AvatarArrived;              // deviceId, notification key, bytes
    public event Action<string, string, byte[]>? TopImageArrived;            // deviceId, notification key, bytes (bigPicture style)
    public event Action<string, string, string, string, byte[]>? MessageImageArrived; // deviceId, notification key, transferId, mimeType, bytes
    public event Action<string, int, bool>? BatteryChanged;                  // deviceId, level 0-100, charging
    public event Action<string, string?, int?>? WifiChanged;                 // deviceId, ssid (null if no location permission on phone), level 0-4
    public event Action<string, IReadOnlyList<CellularLine>>? CellularChanged; // deviceId, one entry per active SIM line
    public event Action<string, int, int>? VolumeChanged;                    // deviceId, level, max (STREAM_MUSIC)
    public event Action<string, bool>? RotationChanged;                     // deviceId, autoRotate on — see docs/PROTOCOL.md's rotation.state
    public event Action<string, bool>? DndChanged;                          // deviceId, active — see docs/PROTOCOL.md's dnd.state
    public event Action<string, IReadOnlyList<AppFilterEntry>>? AppListReceived; // deviceId, apps
    public event Action<string, string, long>? ShareRecentsReceived;           // deviceId, apps JSON [{packageName,label}], updatedAt (ms)
    public event Action<string, string, bool>? AppFilterEntryChanged;        // deviceId, packageName, allowed (arrived from phone)
    public event Action<string>? PairingCodeShown;                           // 6-digit code to confirm on the phone
    public event Action<string, string>? DevicePaired;                      // deviceId, deviceName — a genuinely new pairing
    public event Action<string, string>? DeviceReconnected;                 // deviceId, deviceName — an already-paired device reconnecting
    public event Action<IReadOnlyList<DeviceListEntry>>? DeviceListChanged;
    public event Action<string, byte[]>? WallpaperArrived;                  // deviceId, bytes
    public event Action<string, MediaPlaybackState?>? MediaStateChanged;    // deviceId, null = no active session
    public event Action<PcMediaState?>? PcMediaStateChanged;                // this PC's own local session (Windows SMTC) — not per-device, there's only one PC
    public event Action<string, ScreenShareStats>? ScreenShareStatsChanged;         // live fps/resolution scraped from scrcpy's own output
    public event Action<string, float>? AudioLevelChanged;                         // real WASAPI peak level (0.0-1.0) of whichever scrcpy session is forwarding phone audio
    public event Action<string>? AudioLevelUnavailable;                            // AudioLevelMonitor never found a matching Windows audio session to measure
    public event Action<string>? ScreenShareStopped;                                // the scrcpy window/process ended, for any reason
    public event Action<string, string>? AppShareStopped;
    public event Action<string, string, string>? AppShareStateChanged;            // deviceId, packageName, "pc" | "phone"                   // deviceId, packageName — a single-app share window closed
    public event Action<string>? AudioShareStopped;                                 // the audio-only scrcpy process ended, for any reason
    public event Action<string, string, string?, string?>? SharePhoneRequested; // deviceId, "screen" | "audio" | "app", packageName, label — the phone asked to start sharing
    public event Action<string, string>? CallActive;                        // deviceId, deviceName — Telecom shows a call in progress (direction unknown, see docs/PROTOCOL.md's call.state)
    public event Action<string>? CallEnded;                                 // deviceId — the call session Telecom was reporting is gone
    public event Action<string, bool, int?, bool, bool>? CallStatusChanged;   // deviceId, answered, elapsedSeconds (when just answered), speaker, muted — see docs/PROTOCOL.md's call.status
    public event Action<bool, string, string>? CallControlResult;           // success, action ("answer"|"decline"|"hangup"), method ("telecom"|"accessibility"|reason when !success) — see docs/PROTOCOL.md's call.control.result
    public event Action<bool, string?>? OpenNotificationResult;             // success, reason (only set when !success) — see docs/PROTOCOL.md's notification.open.result

    // Which device the currently active (or just-ended) share session belongs to — ScreenShareService
    // itself has no concept of "device", it just runs a process, so this is what lets share.status
    // updates go back to the right phone instead of broadcasting to whichever one happens to be paired.

    public string QrDataUrl => QrCodeGenerator.ToDataUrl(NetworkInfo.GetLocalIPv4());
    public string LocalIp => NetworkInfo.GetLocalIPv4();

    public MainViewModel(TcpServer server)
    {
        _server = server;
        _server.Log += PcDiagLog.Write;
        _server.DeviceConnected += (deviceId, remoteEndpoint) =>
        {
            lock (_connectedDeviceIds) _connectedDeviceIds.Add(deviceId);
            // remoteEndpoint is "ip:port" (IPv4) or "[ip]:port" (IPv6) — only the bare IP is
            // useful to ScreenShareService, which appends its own fixed ADB port.
            var ip = remoteEndpoint.Contains(']')
                ? remoteEndpoint[1..remoteEndpoint.IndexOf(']')]
                : remoteEndpoint.Split(':')[0];
            if (ip.Length > 0) lock (_deviceIps) _deviceIps[deviceId] = ip;
            Post(RefreshDeviceList);
            // Re-announces this phone's own real share state on every (re)connect, not just
            // when it changes — the "stopped" share.status a disconnect-triggered StopAllSharing
            // sends (see DeviceDisconnected below) has nowhere to go while the phone is actually
            // offline, so without this its "Compartiendo..." status notification could stay
            // stuck showing stale state indefinitely after reconnecting. False for a device that
            // was never the one sharing in the first place, which is the common case and also
            // exactly right — nothing to correct there.
            SendShareStatus(deviceId);
            SendPhonePrefs(deviceId);
            SendPcAddresses(deviceId);
            bool wantLevels;
            lock (_levelsWanted) wantLevels = _levelsWanted.Contains(deviceId);
            if (wantLevels) _ = _server.SendAsync(deviceId, "media.levels.request", new { on = true });
        };
        _server.DeviceDisconnected += deviceId =>
        {
            lock (_connectedDeviceIds) _connectedDeviceIds.Remove(deviceId);
            Post(RefreshDeviceList);
            // The phone that just dropped is exactly the one a still-"active" screen/audio share
            // would be talking to — leaving scrcpy running against a now-unreachable phone is
            // exactly what left a phone's own audio capture stuck open until a reboot (see
            // TcpServer's heartbeat watchdog, which is what actually notices this case: a phone
            // that silently vanishes from the network never causes a normal socket error on its
            // own). A reconnect after this always starts from zero — nothing shared, nothing
            // running — never inherits whatever was active before the drop.
            Post(() => StopAllSharing(deviceId));
        };
        _server.MessageReceived += OnMessageReceived;
        _server.ImageReceived += OnImageReceived;
        ScreenShareService.StatsUpdated += (deviceId, stats) => Post(() => ScreenShareStatsChanged?.Invoke(deviceId, stats));
        ScreenShareService.AudioLevelChanged += (deviceId, level) => Post(() => AudioLevelChanged?.Invoke(deviceId, level));
        ScreenShareService.AudioLevelUnavailable += deviceId => Post(() => AudioLevelUnavailable?.Invoke(deviceId));
        ScreenShareService.Stopped += deviceId => Post(() => { ScreenShareStopped?.Invoke(deviceId); SendShareStatus(deviceId); });
        ScreenShareService.AppStateChanged += (deviceId, pkg, state) =>
            Post(() => { AppShareStateChanged?.Invoke(deviceId, pkg, state); SendShareStatus(deviceId); });
        ScreenShareService.AppStopped += (deviceId, pkg) => Post(() => { AppShareStopped?.Invoke(deviceId, pkg); SendShareStatus(deviceId); });
        ScreenShareService.AudioStopped += deviceId => Post(() => { AudioShareStopped?.Invoke(deviceId); SendShareStatus(deviceId); });
        PcMediaService.StateChanged += state => Post(() => PcMediaStateChanged?.Invoke(state));
        PcMediaService.Start();
    }

    /// <summary>Tells the phone what's currently sharing (see docs/PROTOCOL.md's share.status)
    /// so it can show/update/hide its own "Compartiendo..." notification — sent to whichever
    /// device the active (or just-ended) session belongs to. No-ops if nothing's ever shared
    /// this session, since there's no device to tell.</summary>
    private void SendShareStatus(string deviceId)
    {
        var screenSharing = ScreenShareService.IsVideoActive(deviceId);
        var audioSharing = ScreenShareService.IsAudioActive(deviceId);
        // One phone notification per shared app, each with its own "Detener" (see docs/PROTOCOL.md).
        var apps = ScreenShareService.ActiveAppsWithLabels(deviceId)
            .Select(a => new { packageName = a.Package, label = a.Label, state = a.State }).ToList();
        _ = _server.SendAsync(deviceId, "share.status", new { screenSharing, audioSharing, apps });
    }

    /// <summary>Starts the UDP responder so phones on the LAN can find this PC without the
    /// user typing an IP (see docs/PROTOCOL.md Discovery). Safe to call once at startup.</summary>
    public void StartDiscovery(string? pcId = null) => _discoveryResponder.Start(Environment.MachineName, TcpServer.DefaultPort, pcId);

    public void Shutdown()
    {
        _discoveryResponder.Dispose();
        PcMediaService.Stop();
    }

    public IReadOnlyList<DeviceListEntry> GetPairedDevices() =>
        _pairedDeviceStore.List()
            .Select(d =>
            {
                DeviceBadge.Set(d.DeviceId, d.BadgeText, d.BadgeColor);
                var badge = DeviceBadge.Resolve(d.DeviceId, d.DeviceName);
                return new DeviceListEntry(d.DeviceId, d.DeviceName, _connectedDeviceIds.Contains(d.DeviceId),
                    badge.Text, badge.Color, badge.Custom);
            })
            .ToList();

    /// <summary>Real cancel on the phone — not just removing it from this view. The phone
    /// confirms via its own notification.removed once the cancel actually takes effect.
    /// A synthetic "share.send" card (see HandleIncomingShare) never existed as a real
    /// Android notification, so there's nothing for the phone to cancel and no
    /// notification.removed would ever come back — removed locally instead.</summary>
    public Task DeleteNotification(string deviceId, string key)
    {
        if (key.StartsWith(SharedCardKeyPrefix, StringComparison.Ordinal))
        {
            Post(() => NotificationRemoved?.Invoke(deviceId, key));
            return Task.CompletedTask;
        }
        return _server.SendAsync(deviceId, "delete.request", new { key });
    }

    /// <summary>Only ever cancels notifications this app actually mirrored — never touches
    /// unrelated system notifications (see docs/PROTOCOL.md delete.request.all).</summary>
    public Task DeleteAll(string deviceId) => _server.SendAsync(deviceId, "delete.request.all", new { });

    /// <summary>The real tap-through behavior a touch on the notification itself would trigger
    /// on the phone (see NotiLinkAccessibilityService) — not a generic app launch. A synthetic
    /// "share.send" card has no real notification behind it to open, same reasoning as
    /// DeleteNotification above.</summary>
    public Task OpenNotification(string deviceId, string key) =>
        key.StartsWith(SharedCardKeyPrefix, StringComparison.Ordinal) ? Task.CompletedTask : _server.SendAsync(deviceId, "notification.open", new { key });

    /// <summary>The now-playing bar's own "abrir en el dispositivo" button — see
    /// docs/PROTOCOL.md's media.open for why this has no key the way OpenNotification does.</summary>
    public Task OpenMedia(string deviceId) => _server.SendAsync(deviceId, "media.open", new { });

    /// <summary>Fires the notification's own real action — e.g. WhatsApp's "Responder" or
    /// "Marcar como leído" — nothing invented. replyText is null for actions with no reply input.</summary>
    public Task InvokeAction(string deviceId, string key, int actionIndex, string? replyText) =>
        _server.SendAsync(deviceId, "action.invoke", new { key, actionIndex, replyText });

    public void RenameDevice(string deviceId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _pairedDeviceStore.Rename(deviceId, name.Trim());
        RefreshDeviceList();
    }

    /// <summary>The phone's identifier shown before its name and on the taskbar icon of
    /// everything shared from it. Empty text/color = back to automatic. Windows already open
    /// get the new icon too.</summary>
    public void SetDeviceBadge(string deviceId, string? text, string? color)
    {
        var device = _pairedDeviceStore.Get(deviceId);
        if (device == null) return;
        var t = DeviceBadge.NormalizeText(text);
        var c = DeviceBadge.NormalizeColor(color);
        _pairedDeviceStore.SetBadge(deviceId, t, c);
        DeviceBadge.Set(deviceId, t, c);
        RefreshDeviceList();
        ScreenShareService.RefreshDeviceIcons(deviceId, device.DeviceName);
    }

    /// <summary>Revokes the pairing (its saved token stops being valid) and drops the socket
    /// if it's currently connected — the phone falls back to the 6-digit pairing flow again on
    /// its next connection attempt, same as a brand-new device. Frees up re-pairing a device
    /// that got added twice (e.g. reinstalled and paired again under what looks like a dupe).</summary>
    public void RemoveDevice(string deviceId)
    {
        _pairedDeviceStore.Revoke(deviceId);
        _server.Disconnect(deviceId);
        lock (_connectedDeviceIds) _connectedDeviceIds.Remove(deviceId);
        RefreshDeviceList();
    }

    /// <summary>Bidirectional — the phone applies this the same way whether it arrived from
    /// here or the user toggled it locally on their own Apps screen (see docs/PROTOCOL.md).</summary>
    public Task SetAppAllowed(string deviceId, string packageName, bool allowed) =>
        _server.SendAsync(deviceId, "app.filter.update", new { packageName, allowed });

    /// <summary>PC-initiated disconnect: tells the phone to mark itself manually disconnected
    /// (so it stops auto-reconnecting) and drop the socket. Reconnecting is one-directional —
    /// once the socket is actually closed there's no channel left to reach the phone over, so
    /// that side has to happen from the phone itself (its own persistent-notification toggle,
    /// or just reopening the app).</summary>
    public Task ToggleConnection(string deviceId, bool disconnect) =>
        _server.SendAsync(deviceId, "connection.toggle", new { disconnect });

    /// <summary>"Buscar celular" — rings/vibrates the phone for a few seconds so it can be
    /// located, the same idea as Android's own Find My Device.</summary>
    public Task RingDevice(string deviceId) => _server.SendAsync(deviceId, "device.ring", new { });

    /// <summary>The header's volume slider — see docs/PROTOCOL.md's volume.set.</summary>
    public Task SendVolume(string deviceId, int level) => _server.SendAsync(deviceId, "volume.set", new { level });

    /// <summary>The header's "No molestar" toggle, next to the volume button — see
    /// docs/PROTOCOL.md's dnd.set. Silently no-ops on the phone if it never granted the separate
    /// Notification Policy Access permission; nothing the PC side can detect or work around.</summary>
    /// <summary>Answer/decline/hang up a call on the phone from the call bar — see
    /// docs/PROTOCOL.md's call.control. [action] is "answer" | "hangup" (decline and hang up are
    /// the same request: end whatever call is current, ringing or connected).</summary>
    public Task SendCallControl(string deviceId, string action) => _server.SendAsync(deviceId, "call.control", new { action });

    /// <summary>The PC's "Recientes" for this phone (newest first) — see docs/PROTOCOL.md share.recents.</summary>
    public Task SendShareRecents(string deviceId, JsonElement apps, long updatedAt) =>
        _server.SendAsync(deviceId, "share.recents", new { apps, updatedAt });

    public Task SendRotation(string deviceId, bool active) => _server.SendAsync(deviceId, "rotation.set", new { active });

    public Task SendDnd(string deviceId, bool active) => _server.SendAsync(deviceId, "dnd.set", new { active });

    /// <summary>Play/pause/next/previous from the persistent "now playing" bar — fires the
    /// phone's real MediaController.TransportControls (see docs/PROTOCOL.md's media.control),
    /// never a fake/local-only toggle.</summary>
    public Task SendMediaControl(string deviceId, string action, long? position = null) =>
        position.HasValue
            ? _server.SendAsync(deviceId, "media.control", new { action, position = position.Value })
            : _server.SendAsync(deviceId, "media.control", new { action });

    /// <summary>The now-playing bar's PC-mode toggle — same button, same UI, but this never
    /// touches the network at all: the session it's controlling is already running on this same
    /// machine (see Services/PcMediaService.cs), so there's no phone round-trip to wait on.</summary>
    public void SendPcMediaControl(string action, long? position = null)
    {
        switch (action)
        {
            case "playPause": PcMediaService.PlayPause(); break;
            case "next": PcMediaService.Next(); break;
            case "previous": PcMediaService.Previous(); break;
            case "shuffle": PcMediaService.ToggleShuffle(); break;
            case "seek": if (position.HasValue) PcMediaService.Seek(position.Value); break;
        }
    }

    /// <summary>See docs/PROTOCOL.md's media.state. album art (if any) arrives as a separate
    /// binary frame right after — the bar renders immediately from just the text fields, then
    /// picks up the art once OnImageReceived resolves it, same two-step pattern as every other
    /// image on the wire.</summary>
    private void HandleMediaState(string deviceId, JsonElement payload)
    {
        var active = payload.TryGetProperty("active", out var a) && a.GetBoolean();
        if (!active)
        {
            lock (_lastMediaState) _lastMediaState.Remove(deviceId);
            Post(() => MediaStateChanged?.Invoke(deviceId, null));
            return;
        }

        var packageName = payload.GetProperty("packageName").GetString() ?? "";
        var title = payload.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var artist = payload.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "";
        var hasNewArt = payload.TryGetProperty("albumArtTransferId", out var tid) &&
            tid.ValueKind == JsonValueKind.String && tid.GetString() is { };

        // Confirmed intermittent on the phone side: resolving Spotify's album art is a
        // cross-process content-provider call redone on every play/pause toggle, which
        // occasionally came back empty for no fixed pattern (a momentarily-busy provider, not
        // the art actually changing) — this update would arrive with no albumArtTransferId even
        // though it's plainly still the same track. Without this, that single failed resolution
        // was enough to blank out art the bar had correctly shown a second earlier. Carrying the
        // previous art forward whenever the track identity hasn't changed keeps the bar showing
        // what it already knows to be correct instead of flashing to no-art on a fluke.
        byte[]? albumArtBytes = null;
        lock (_lastMediaState)
        {
            if (!hasNewArt && _lastMediaState.TryGetValue(deviceId, out var previous) &&
                previous.PackageName == packageName && previous.Title == title && previous.Artist == artist)
                albumArtBytes = previous.AlbumArtBytes;
        }

        var state = new MediaPlaybackState
        {
            PackageName = packageName,
            AppLabel = payload.GetProperty("appLabel").GetString() ?? "",
            Title = title,
            Artist = artist,
            Album = payload.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "",
            IsPlaying = payload.TryGetProperty("isPlaying", out var ip) && ip.GetBoolean(),
            Position = payload.TryGetProperty("position", out var pos) ? pos.GetInt64() : 0,
            Duration = payload.TryGetProperty("duration", out var dur) ? dur.GetInt64() : 0,
            CanPlayPause = payload.TryGetProperty("canPlayPause", out var cpp) && cpp.GetBoolean(),
            CanNext = payload.TryGetProperty("canNext", out var cn) && cn.GetBoolean(),
            CanPrevious = payload.TryGetProperty("canPrevious", out var cpv) && cpv.GetBoolean(),
            CanFavorite = payload.TryGetProperty("canFavorite", out var cf) && cf.GetBoolean(),
            IsFavorite = payload.TryGetProperty("isFavorite", out var isf) && isf.GetBoolean(),
            CanShuffle = payload.TryGetProperty("canShuffle", out var cs) && cs.GetBoolean(),
            IsShuffleOn = payload.TryGetProperty("isShuffleOn", out var iso) && iso.GetBoolean(),
            IsSmartShuffle = payload.TryGetProperty("isSmartShuffle", out var iss) && iss.GetBoolean(),
            CanSeek = payload.TryGetProperty("canSeek", out var cse) && cse.GetBoolean(),
            AlbumArtBytes = albumArtBytes,
        };

        lock (_lastMediaState) _lastMediaState[deviceId] = state;
        Post(() => MediaStateChanged?.Invoke(deviceId, state));

        if (hasNewArt) lock (_pendingMediaArt) _pendingMediaArt[tid.GetString()!] = deviceId;
    }

    /// <summary>"Compartir pantalla" — mirrors the phone's screen via the vendored scrcpy
    /// (see ScreenShareService, CLAUDE.md's "Screen sharing" section). Needs the phone's IP,
    /// which is exactly the remote endpoint of its own NotiLink TCP connection — no separate
    /// discovery needed, tracked from DeviceConnected above.</summary>
    public async Task<(bool Success, string Message)> StartScreenShare(
        string deviceId, int maxSize, int maxFps, int bitRateMbps,
        string videoCodec, int bufferMs, bool turnScreenOff, bool stayAwake, bool audio, bool dimPhone = false)
    {
        string? ip;
        lock (_deviceIps) _deviceIps.TryGetValue(deviceId, out ip);
        if (ip == null)
            return (false, "Este dispositivo no está conectado ahora mismo.");
        var deviceName = _pairedDeviceStore.Get(deviceId)?.DeviceName ?? "el celular";
        var result = await ScreenShareService.StartAsync(deviceId, ip, deviceName, maxSize, maxFps, bitRateMbps, videoCodec, bufferMs, turnScreenOff, stayAwake, audio, dimPhone).ConfigureAwait(false);
        if (result.Success) Post(() => SendShareStatus(deviceId));
        return result;
    }

    /// <summary>"Compartir audio" — routes just the phone's audio (calls, music, video sound)
    /// to this PC via scrcpy's audio-only mode, no video pipeline involved. Built for the case
    /// the user actually described: answering a call, or listening to something on the phone,
    /// without physically swapping headphones between the two devices every time.</summary>
    public async Task<(bool Success, string Message)> StartAudioShare(string deviceId)
    {
        string? ip;
        lock (_deviceIps) _deviceIps.TryGetValue(deviceId, out ip);
        if (ip == null)
            return (false, "Este dispositivo no está conectado ahora mismo.");
        var deviceName = _pairedDeviceStore.Get(deviceId)?.DeviceName ?? "el celular";
        var result = await ScreenShareService.StartAudioOnlyAsync(deviceId, ip, deviceName).ConfigureAwait(false);
        if (result.Success) Post(() => SendShareStatus(deviceId));
        return result;
    }

    public void StopAudioShare(string deviceId) => ScreenShareService.StopAudio(deviceId);

    /// <summary>The PC's own "detener" for video mirroring — until now the only way to stop a
    /// screen-share session was to close scrcpy's own mirror window (or use the phone's
    /// "Detener" on its sharing-status notification, which calls StopAllSharing instead). Scoped
    /// to just the video session, same as StopAudioShare is scoped to just audio — stopping one
    /// must never tear down the other if both happen to be running independently.</summary>
    public void StopScreenShare(string deviceId) => ScreenShareService.StopVideo(deviceId);

    /// <summary>The phone's "Detener" action on its sharing-status notification stops
    /// whatever's actually active — unlike StopAudioShare (the PC's own audio toggle button,
    /// scoped to just that one feature), the phone only ever sees one combined notification
    /// covering both, so its one stop action needs to cover both too.</summary>
    public void StopAllSharing(string deviceId)
    {
        ScreenShareService.StopVideo(deviceId);
        ScreenShareService.StopAudio(deviceId);
        ScreenShareService.StopAllApps(deviceId);
    }

    /// <summary>"Compartir una app" — only [packageName]'s UI, on its own virtual display.</summary>
    internal async Task<(bool Success, string Message)> StartAppShare(
        string deviceId, string packageName, string appLabel, int maxSize, int maxFps, int bitRateMbps, string videoCodec, int bufferMs,
        ScreenShareService.AppShareOptions options)
    {
        string? ip;
        lock (_deviceIps) _deviceIps.TryGetValue(deviceId, out ip);
        if (ip == null) return (false, "Este dispositivo no está conectado ahora mismo.");
        var deviceName = _pairedDeviceStore.Get(deviceId)?.DeviceName ?? "el celular";
        var result = await ScreenShareService.StartAppAsync(deviceId, ip, deviceName, packageName, appLabel,
            maxSize, maxFps, bitRateMbps, videoCodec, bufferMs, options, GetIconBytes(packageName)).ConfigureAwait(false);
        if (result.Success) Post(() => SendShareStatus(deviceId));
        return result;
    }

    public void StopAppShare(string deviceId, string packageName) => ScreenShareService.StopApp(deviceId, packageName);

    private int _goodbyeSent;

    // -1 = the page hasn't reported it yet (nothing is sent to phones until it has).
    private int _phoneStatusAutoHideSeconds = -1;

    /// <summary>PC Ajustes → phone: hide the phone's connected/disconnected status notification
    /// after [seconds] (0 = never). Kept here so phones that connect later get it too.</summary>
    public void SetPhoneStatusAutoHide(int seconds)
    {
        _phoneStatusAutoHideSeconds = Math.Clamp(seconds, 0, 3600);
        SendPhonePrefsToAll();
    }

    // null = the page hasn't reported it yet.
    private string? _phoneAccent;

    /// <summary>PC accent color ("#RRGGBB") → the phone app themes itself with it.</summary>
    public void SetPhoneAccent(string accent)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(accent ?? "", "^#[0-9A-Fa-f]{6}$")) return;
        _phoneAccent = accent!.ToUpperInvariant();
        SendPhonePrefsToAll();
    }

    /// <summary>Every address this PC has (LAN + Tailscale) → the phone, so it can reach the PC
    /// again from another network (docs/PROTOCOL.md pc.addresses). On connect and whenever this
    /// PC's network changes.</summary>
    public void BroadcastPcAddresses()
    {
        string[] ids;
        lock (_connectedDeviceIds) ids = _connectedDeviceIds.ToArray();
        foreach (var id in ids) SendPcAddresses(id);
    }

    private void SendPcAddresses(string deviceId)
    {
        var addresses = NetworkInfo.GetAddressesForPhone().Select(a => new { ip = a.Ip, kind = a.Kind, adapter = a.Adapter }).ToList();
        if (addresses.Count > 0) _ = _server.SendAsync(deviceId, "pc.addresses", new { addresses });
    }

    /// <summary>The phone's network (`phone.network`) plus how it reaches this PC: "vpn" when its
    /// address is a Tailscale/CGNAT one, else "lan".</summary>
    public event Action<string, bool, string, bool, string>? PhoneNetworkChanged;

    /// <summary>Band levels (bass, mid, treble 0..1) of what the phone is playing — see
    /// docs/PROTOCOL.md media.levels.</summary>
    public event Action<string, float, float, float>? MediaLevels;

    /// <summary>Asks the phone to start/stop streaming media.levels (only while the now-playing
    /// bar is visible and playing, so it's not sending ~15 messages a second for nothing).</summary>
    public void RequestMediaLevels(string deviceId, bool on)
    {
        lock (_levelsWanted) { if (on) _levelsWanted.Add(deviceId); else _levelsWanted.Remove(deviceId); }
        _ = _server.SendAsync(deviceId, "media.levels.request", new { on });
    }

    // Phones the now-playing bar wants levels from — re-asked on reconnect (a request sent while
    // the phone was momentarily offline is simply dropped).
    private readonly HashSet<string> _levelsWanted = new(); // deviceId, metered, transport, vpn, route

    private void SendPhonePrefsToAll()
    {
        string[] ids;
        lock (_connectedDeviceIds) ids = _connectedDeviceIds.ToArray();
        foreach (var id in ids) SendPhonePrefs(id);
    }

    private void SendPhonePrefs(string deviceId)
    {
        var prefs = new Dictionary<string, object>();
        if (_phoneStatusAutoHideSeconds >= 0) prefs["statusAutoHideSeconds"] = _phoneStatusAutoHideSeconds;
        if (_phoneAccent != null) prefs["accent"] = _phoneAccent;
        if (prefs.Count > 0) _ = _server.SendAsync(deviceId, "phone.prefs", prefs);
    }

    /// <summary>Tells every connected phone the PC is going away ([reason]: "exit" | "shutdown" |
    /// "logoff" | "sleep") so it hides its NotiLink notifications until the next reconnect (see
    /// docs/PROTOCOL.md's pc.bye). Blocks up to ~1.5 s — called right before the app or Windows
    /// goes down, where there's no later chance to send it. Sent once per shutdown; "sleep" can
    /// repeat (each suspend is its own goodbye).</summary>
    public void SayGoodbye(string reason)
    {
        if (reason != "sleep" && Interlocked.Exchange(ref _goodbyeSent, 1) == 1) return;
        string[] ids;
        lock (_connectedDeviceIds) ids = _connectedDeviceIds.ToArray();
        if (ids.Length == 0) return;
        try
        {
            Task.Run(() => Task.WhenAll(ids.Select(id => _server.SendAsync(id, "pc.bye", new { reason })))).Wait(1500);
        }
        catch (Exception)
        {
            // Best-effort: a phone that misses it goes quiet on its own after a couple of minutes.
        }
        PcDiagLog.Write($"pc.bye ({reason}) sent to {ids.Length} device(s)");
    }

    /// <summary>PC->phone half of text/link sharing (see docs/PROTOCOL.md's share.send) —
    /// the phone posts this as a real native notification of its own. If the text is a URL,
    /// fetches its Open Graph title/description/image first (same LinkPreviewService the PC's
    /// own notification cards use) so the phone's notification shows a real preview instead of
    /// a bare link — best-effort: any fetch failure just falls back to the plain-text send.</summary>
    public async Task ShareToDevice(string deviceId, string text)
    {
        var isUrl = Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
        if (!isUrl)
        {
            await _server.SendAsync(deviceId, "share.send", new { text });
            return;
        }

        var preview = await LinkPreviewService.FetchAsync(text);
        var imageBytes = !string.IsNullOrEmpty(preview?.ImageUrl) ? await TryDownloadPreviewImageAsync(preview.ImageUrl) : null;

        if (imageBytes != null)
        {
            await _server.SendWithBinaryAsync(deviceId, "share.send", new
            {
                text,
                title = preview?.Title,
                description = preview?.Description,
                transferId = Guid.NewGuid().ToString(),
            }, imageBytes);
        }
        else
        {
            await _server.SendAsync(deviceId, "share.send", new { text, title = preview?.Title, description = preview?.Description });
        }
    }

    private static readonly System.Net.Http.HttpClient PreviewImageHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static async Task<byte[]?> TryDownloadPreviewImageAsync(string url)
    {
        try
        {
            var bytes = await PreviewImageHttp.GetByteArrayAsync(url);
            // A preview image this large is almost certainly not actually a small OG thumbnail —
            // skip it rather than pushing something unexpectedly big through the socket.
            return bytes.Length <= 4 * 1024 * 1024 ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Prefix for synthetic cards this ViewModel invents itself (currently just
    /// incoming shares) rather than mirroring a real phone-side notification — lets
    /// DeleteNotification recognize them and skip the round trip to the phone.</summary>
    private const string SharedCardKeyPrefix = "share-";

    /// <summary>The phone->PC half of text/link sharing (see docs/PROTOCOL.md's share.send) —
    /// renders the shared text as a card indistinguishable from a real mirrored notification
    /// (own icon, same list, same delete/link-preview behavior), rather than a bespoke UI
    /// surface, since NotificationUpserted already does everything this needs.</summary>
    private void HandleIncomingShare(string deviceId, string text)
    {
        var item = new NotificationItem
        {
            Key = $"{SharedCardKeyPrefix}{Guid.NewGuid()}",
            PackageName = "com.notilink.app",
            AppLabel = "NotiLink",
            Title = "Compartido desde el celular",
            Text = text,
            PostedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsClearable = true,
            IconBytes = AppInfo.IconBytes,
            IsSharedText = true,
        };
        Post(() => NotificationUpserted?.Invoke(deviceId, item));
    }

    private void RefreshDeviceList() => DeviceListChanged?.Invoke(GetPairedDevices());

    /// <summary>Overwrites (not appends) — the phone's own copy already self-trims and this
    /// message always carries its full current content, so replacing keeps this file from
    /// growing unbounded across many reconnects while still always holding the latest
    /// snapshot. See docs/PROTOCOL.md's diag.log section.
    ///
    /// Keyed by deviceId ALONE, not deviceId+deviceName: an earlier version included the
    /// current device name in the filename, so every rename (or a name arriving as the
    /// "dispositivo" fallback before the real one synced) silently orphaned the previous file
    /// under its old name instead of updating it — leaving a pile of stale, same-device log
    /// files sitting next to the current one with older timestamps, easy to open by mistake via
    /// "Abrir carpeta de logs". The device's current display name is still written as the first
    /// line for readability, and any leftover same-device files under old names are cleaned up
    /// on write so the folder doesn't keep accumulating them.</summary>
    private void WriteDiagLog(string deviceId, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink", "logs");
            Directory.CreateDirectory(dir);
            var deviceName = _pairedDeviceStore.Get(deviceId)?.DeviceName ?? "dispositivo";
            var path = Path.Combine(dir, $"{deviceId}.log");
            File.WriteAllText(path, $"# {deviceName} ({deviceId})\r\n{text}");

            foreach (var stale in Directory.EnumerateFiles(dir, $"*_{deviceId}.log"))
            {
                try { File.Delete(stale); } catch (Exception) { /* best-effort cleanup */ }
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostic aid — never worth taking the app down over.
        }
    }

    private void OnMessageReceived(string deviceId, string type, JsonElement payload)
    {
        switch (type)
        {
            case "notification.posted":
                var packageName = payload.GetProperty("packageName").GetString() ?? "";
                var iconBytes = GetIconBytes(packageName);
                var item = NotificationItem.FromPayload(payload, iconBytes);

                if (item.HasAvatar && item.AvatarTransferId is { } avatarTid)
                    _pendingAvatars[avatarTid] = (deviceId, item.Key);
                if (item.HasImage && item.ImageTransferId is { } imgTid)
                    _pendingNotificationImages[imgTid] = (deviceId, item.Key);
                if (item.Messages != null)
                    foreach (var m in item.Messages)
                        if (m.ImageTransferId is { } msgTid)
                            _pendingMessageImages[msgTid] = (deviceId, item.Key, m.ImageMimeType ?? "image/jpeg");

                Post(() => NotificationUpserted?.Invoke(deviceId, item));
                break;

            case "notification.removed":
                var key = payload.GetProperty("key").GetString();
                if (key != null) Post(() => NotificationRemoved?.Invoke(deviceId, key));
                break;

            case "notification.sync":
                if (payload.TryGetProperty("keys", out var syncKeysEl) && syncKeysEl.ValueKind == JsonValueKind.Array)
                {
                    var syncKeys = syncKeysEl.EnumerateArray()
                        .Select(k => k.GetString())
                        .Where(k => k != null)
                        .Cast<string>()
                        .ToList();
                    Post(() => NotificationSyncReceived?.Invoke(deviceId, syncKeys));
                }
                break;

            case "icon.data":
                var iconPackage = payload.GetProperty("packageName").GetString() ?? "";
                var transferId = payload.GetProperty("transferId").GetString() ?? "";
                if (transferId.Length > 0)
                    _pendingIconTransfers[transferId] = (deviceId, iconPackage);
                break;

            case "battery.update":
                var level = payload.TryGetProperty("level", out var lvl) ? lvl.GetInt32() : 0;
                var charging = payload.TryGetProperty("charging", out var chg) && chg.GetBoolean();
                Post(() => BatteryChanged?.Invoke(deviceId, level, charging));

                var wifiSsid = payload.TryGetProperty("wifiSsid", out var ssidEl) && ssidEl.ValueKind == JsonValueKind.String
                    ? ssidEl.GetString()
                    : null;
                var wifiLevel = payload.TryGetProperty("wifiLevel", out var wlEl) && wlEl.ValueKind == JsonValueKind.Number
                    ? wlEl.GetInt32()
                    : (int?)null;
                Post(() => WifiChanged?.Invoke(deviceId, wifiSsid, wifiLevel));

                var cellularLines = new List<CellularLine>();
                if (payload.TryGetProperty("cellular", out var cellArr) && cellArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in cellArr.EnumerateArray())
                    {
                        var carrierName = line.TryGetProperty("carrierName", out var cn) ? cn.GetString() ?? "SIM" : "SIM";
                        var cellLevel = line.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0;
                        cellularLines.Add(new CellularLine(carrierName, cellLevel));
                    }
                }
                Post(() => CellularChanged?.Invoke(deviceId, cellularLines));
                break;

            case "volume.state":
                var volumeLevel = payload.TryGetProperty("level", out var vl) ? vl.GetInt32() : 0;
                var volumeMax = payload.TryGetProperty("max", out var vm) ? vm.GetInt32() : 0;
                Post(() => VolumeChanged?.Invoke(deviceId, volumeLevel, volumeMax));
                break;

            case "rotation.state":
                var rotActive = payload.TryGetProperty("active", out var ra) && ra.GetBoolean();
                Post(() => RotationChanged?.Invoke(deviceId, rotActive));
                break;

            case "dnd.state":
                var dndActive = payload.TryGetProperty("active", out var da) && da.GetBoolean();
                Post(() => DndChanged?.Invoke(deviceId, dndActive));
                break;

            case "app.sync":
                var apps = ParseAppSync(deviceId, payload);
                Post(() => AppListReceived?.Invoke(deviceId, apps));
                break;

            case "app.filter.update":
                // Arrives when the user toggled it on the phone's own Apps screen — this app
                // sends the same message type the other direction (see SetAppAllowed).
                var updatedPackage = payload.GetProperty("packageName").GetString();
                var updatedAllowed = payload.TryGetProperty("allowed", out var al) && al.GetBoolean();
                if (updatedPackage != null) Post(() => AppFilterEntryChanged?.Invoke(deviceId, updatedPackage, updatedAllowed));
                break;

            case "media.state":
                HandleMediaState(deviceId, payload);
                break;

            case "file.send":
                var fileName = payload.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "archivo" : "archivo";
                var mimeType = payload.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "application/octet-stream" : "application/octet-stream";
                var fileTransferId = payload.TryGetProperty("transferId", out var fti) ? fti.GetString() : null;
                if (fileTransferId != null) _pendingFiles[fileTransferId] = (deviceId, fileName, mimeType);
                break;

            case "share.send":
                var sharedText = payload.TryGetProperty("text", out var st) ? st.GetString() ?? "" : "";
                if (sharedText.Length > 0) HandleIncomingShare(deviceId, sharedText);
                break;

            case "diag.log":
                var diagText = payload.TryGetProperty("text", out var dt) ? dt.GetString() ?? "" : "";
                WriteDiagLog(deviceId, diagText);
                break;

            case "wallpaper.data":
                var wallpaperTransferId = payload.GetProperty("transferId").GetString() ?? "";
                if (wallpaperTransferId.Length > 0)
                    _pendingWallpaper[wallpaperTransferId] = deviceId;
                break;

            case "handshake.hello":
                HandleHandshake(deviceId, payload);
                break;

            case "pair.confirm":
                HandlePairConfirm(deviceId, payload);
                break;

            case "media.levels":
                if (payload.TryGetProperty("b", out var lbEl) && payload.TryGetProperty("m", out var lmEl) && payload.TryGetProperty("t", out var ltEl))
                {
                    var lb = (float)lbEl.GetDouble(); var lm = (float)lmEl.GetDouble(); var lt = (float)ltEl.GetDouble();
                    Post(() => MediaLevels?.Invoke(deviceId, lb, lm, lt));
                }
                break;

            case "phone.network":
                var netMetered = payload.TryGetProperty("metered", out var nmEl) && nmEl.ValueKind == JsonValueKind.True;
                var netTransport = payload.TryGetProperty("transport", out var ntEl) ? ntEl.GetString() ?? "unknown" : "unknown";
                var netVpn = payload.TryGetProperty("vpn", out var nvEl) && nvEl.ValueKind == JsonValueKind.True;
                string? phoneIp;
                lock (_deviceIps) _deviceIps.TryGetValue(deviceId, out phoneIp);
                var parts = (phoneIp ?? "").Split('.');
                var viaVpn = parts.Length == 4 && parts[0] == "100" && int.TryParse(parts[1], out var p1) && p1 is >= 64 and <= 127;
                Post(() => PhoneNetworkChanged?.Invoke(deviceId, netMetered, netTransport, netVpn, viaVpn ? "vpn" : "lan"));
                break;

            case "share.recents":
                var recentApps = payload.TryGetProperty("apps", out var raEl) && raEl.ValueKind == JsonValueKind.Array ? raEl.GetRawText() : "[]";
                var recentAt = payload.TryGetProperty("updatedAt", out var rtEl) && rtEl.ValueKind == JsonValueKind.Number ? rtEl.GetInt64() : 0L;
                Post(() => ShareRecentsReceived?.Invoke(deviceId, recentApps, recentAt));
                break;

            case "share.request":
                var shareType = payload.TryGetProperty("type", out var shareTypeEl) ? shareTypeEl.GetString() : null;
                var reqPkg = payload.TryGetProperty("packageName", out var rpkEl) ? rpkEl.GetString() : null;
                var reqLabel = payload.TryGetProperty("label", out var rlEl) ? rlEl.GetString() : null;
                if (shareType is "screen" or "audio" || (shareType == "app" && !string.IsNullOrEmpty(reqPkg)))
                    Post(() => SharePhoneRequested?.Invoke(deviceId, shareType, reqPkg, reqLabel));
                break;

            case "share.stop.request":
                // The phone's screen/audio notification — apps have their own notifications.
                Post(() => { ScreenShareService.StopVideo(deviceId); ScreenShareService.StopAudio(deviceId); });
                break;

            case "share.app.return":
                var returnPkg = payload.TryGetProperty("packageName", out var rpEl) ? rpEl.GetString() : null;
                if (!string.IsNullOrEmpty(returnPkg)) _ = ScreenShareService.ReturnAppAsync(deviceId, returnPkg);
                break;

            case "share.stop.screen":
                Post(() => ScreenShareService.StopVideo(deviceId));
                break;

            case "share.stop.audio":
                Post(() => ScreenShareService.StopAudio(deviceId));
                break;

            case "share.stop.all":
                Post(() => StopAllSharing(deviceId));
                break;

            case "share.stop.app":
                var stopPkg = payload.TryGetProperty("packageName", out var spEl) ? spEl.GetString() : null;
                if (!string.IsNullOrEmpty(stopPkg)) Post(() => ScreenShareService.StopApp(deviceId, stopPkg));
                break;

            case "call.status":
                var stAnswered = payload.TryGetProperty("answered", out var stAEl) && stAEl.ValueKind == JsonValueKind.True;
                int? stElapsed = payload.TryGetProperty("elapsedSeconds", out var stEEl) && stEEl.ValueKind == JsonValueKind.Number ? stEEl.GetInt32() : null;
                var stSpeaker = payload.TryGetProperty("speaker", out var stSEl) && stSEl.ValueKind == JsonValueKind.True;
                var stMuted = payload.TryGetProperty("muted", out var stMEl) && stMEl.ValueKind == JsonValueKind.True;
                Post(() => CallStatusChanged?.Invoke(deviceId, stAnswered, stElapsed, stSpeaker, stMuted));
                break;

            case "call.control.result":
                var ccSuccess = payload.TryGetProperty("success", out var ccsEl) && ccsEl.GetBoolean();
                var ccAction = payload.TryGetProperty("action", out var ccaEl) ? ccaEl.GetString() ?? "" : "";
                var ccMethod = payload.TryGetProperty("method", out var ccmEl) ? ccmEl.GetString() ?? "" : "";
                Post(() => CallControlResult?.Invoke(ccSuccess, ccAction, ccMethod));
                break;

            case "call.state":
                var callState = payload.TryGetProperty("state", out var csEl) ? csEl.GetString() : null;
                if (callState == "active")
                {
                    var callDeviceName = _pairedDeviceStore.Get(deviceId)?.DeviceName ?? "el celular";
                    Post(() => CallActive?.Invoke(deviceId, callDeviceName));
                }
                else if (callState == "ended")
                {
                    Post(() => CallEnded?.Invoke(deviceId));
                }
                break;

            case "notification.open.result":
                var openSuccess = payload.TryGetProperty("success", out var osEl) && osEl.GetBoolean();
                var openReason = payload.TryGetProperty("reason", out var orEl) && orEl.ValueKind == JsonValueKind.String ? orEl.GetString() : null;
                Post(() => OpenNotificationResult?.Invoke(openSuccess, openReason));
                break;
        }
    }

    /// <summary>First message on every connection. A known deviceId+token skips straight to
    /// trusted; anything else starts the 6-digit code flow (see docs/PROTOCOL.md).</summary>
    private void HandleHandshake(string deviceId, JsonElement payload)
    {
        var deviceName = payload.TryGetProperty("deviceName", out var dn) ? dn.GetString() ?? "Teléfono" : "Teléfono";
        var deviceModel = payload.TryGetProperty("deviceModel", out var dm) ? dm.GetString() : null;
        var token = payload.TryGetProperty("pairingToken", out var tok) && tok.ValueKind == JsonValueKind.String
            ? tok.GetString()
            : null;
        // A phone paired with more than one PC sends every token it holds (see the phone's
        // PairingTokenStore) — the one THIS PC issued may not be the latest.
        var candidates = new List<string>();
        if (token != null) candidates.Add(token);
        if (payload.TryGetProperty("pairingTokens", out var toks) && toks.ValueKind == JsonValueKind.Array)
            foreach (var t in toks.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } ts && !candidates.Contains(ts)) candidates.Add(ts);
        var matched = candidates.FirstOrDefault(t => _pairedDeviceStore.IsValidToken(deviceId, t));
        if (matched != null) token = matched;

        var isValidToken = matched != null;
        if (!isValidToken)
        {
            // Diagnostic detail for exactly *why* — was there no stored entry for this deviceId
            // at all (either a genuinely new device, or the store somehow lost track of it), or
            // was there one with a different token (a real mismatch, not a missing-record issue)?
            // Added after a repeatable "the pairing code shows up again for an already-paired
            // phone" report with zero PC-side logging available to explain it — TcpServer.Log
            // existed as an event but nothing subscribed to it until now (see PcDiagLog.Write
            // wired up in this class's constructor).
            var existingForDiag = _pairedDeviceStore.Get(deviceId);
            PcDiagLog.Write($"handshake: deviceId={deviceId} tokenProvided={token != null} " +
                $"storedEntryExists={existingForDiag != null} " +
                $"tokenMatches={(existingForDiag != null && token != null && existingForDiag.Token == token)} " +
                $"provided={(token == null ? "none" : token[..Math.Min(6, token.Length)])}… stored={(existingForDiag == null ? "none" : existingForDiag.Token[..Math.Min(6, existingForDiag.Token.Length)])}… " +
                $"— showing pairing code");
        }

        if (isValidToken)
        {
            // A routine reconnect (app reopened, PC restarted, Wi-Fi blip) — not a new pairing,
            // so unlike DevicePaired this must never switch the active device tab; it's purely
            // informational (see docs/PROTOCOL.md and MainWindow's DeviceReconnected wiring).
            var existing = _pairedDeviceStore.Get(deviceId);
            var displayName = existing?.DeviceName ?? deviceName;
            // Backfill DeviceModel for devices paired before this field existed, and keep it
            // current — it's the fingerprint HandlePairConfirm uses to recognize this same
            // phone if it ever re-pairs under a fresh deviceId.
            if (existing != null && existing.DeviceModel != deviceModel && deviceModel != null)
                _pairedDeviceStore.Save(existing with { DeviceModel = deviceModel });
            Post(() => DeviceReconnected?.Invoke(deviceId, displayName));
            Post(RefreshDeviceList);
            return;
        }

        _pendingPairDeviceNames[deviceId] = (deviceName, deviceModel);
        var code = Random.Shared.Next(0, 1_000_000).ToString("D6");
        _ = _server.SendAsync(deviceId, "pair.code", new { code });
        Post(() => PairingCodeShown?.Invoke(code));
    }

    private void HandlePairConfirm(string deviceId, JsonElement payload)
    {
        if (!_pendingPairDeviceNames.TryGetValue(deviceId, out var pending)) return;
        if (!payload.TryGetProperty("confirmed", out var confirmed) || !confirmed.GetBoolean()) return;

        var (deviceName, deviceModel) = pending;

        // A fresh pair.confirm for a deviceId that's already in the store (confirmed: happens
        // in practice — HandleHandshake's token check failing spuriously for a phone that was
        // never actually un-paired) must never silently reset a name the user already set.
        // FindByDeviceModel alone couldn't catch this: it explicitly excludes this exact
        // deviceId, since it exists to recognize a DIFFERENT deviceId as the same physical phone
        // (e.g. after an uninstall/reinstall) — it was never meant to also cover "this exact
        // device, still known, just went through pairing again for some other reason". Checking
        // this deviceId's own existing entry first covers that gap; FindByDeviceModel remains
        // the fallback for the genuine cross-deviceId reinstall case, and only that case's stale
        // entry gets revoked (revoking this same deviceId's own entry would be pointless — Save
        // below overwrites it anyway).
        var existingSameId = _pairedDeviceStore.Get(deviceId);
        var priorDevice = existingSameId ?? _pairedDeviceStore.FindByDeviceModel(deviceModel, deviceId);
        var resolvedName = priorDevice?.DeviceName ?? deviceName;
        if (priorDevice != null && priorDevice.DeviceId != deviceId) _pairedDeviceStore.Revoke(priorDevice.DeviceId);

        var token = Guid.NewGuid().ToString("N");
        _pairedDeviceStore.Save(new PairedDevice(deviceId, resolvedName, token, deviceModel, priorDevice?.BadgeText, priorDevice?.BadgeColor));
        _ = _server.SendAsync(deviceId, "pair.token", new { token });
        _pendingPairDeviceNames.Remove(deviceId);

        Post(() => DevicePaired?.Invoke(deviceId, resolvedName));
        Post(RefreshDeviceList);
    }

    private List<AppFilterEntry> ParseAppSync(string deviceId, JsonElement payload)
    {
        var result = new List<AppFilterEntry>();
        if (!payload.TryGetProperty("apps", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var a in arr.EnumerateArray())
        {
            var packageName = a.TryGetProperty("packageName", out var p) ? p.GetString() ?? "" : "";
            if (packageName.Length == 0) continue;
            var label = a.TryGetProperty("label", out var l) ? l.GetString() ?? packageName : packageName;
            var allowed = !a.TryGetProperty("allowed", out var al) || al.GetBoolean();
            var isSystemApp = a.TryGetProperty("isSystemApp", out var sys) && sys.GetBoolean();
            var iconBytes = GetIconBytes(packageName);

            if (iconBytes == null)
            {
                var transferId = Guid.NewGuid().ToString();
                _pendingIconTransfers[transferId] = (deviceId, packageName);
                _ = _server.SendAsync(deviceId, "icon.request", new { packageName, transferId });
            }

            result.Add(new AppFilterEntry { PackageName = packageName, Label = label, Allowed = allowed, IconBytes = iconBytes, IsSystemApp = isSystemApp });
        }
        return result;
    }

    private void OnImageReceived(string deviceId, string transferId, byte[] bytes)
    {
        if (_pendingAvatars.Remove(transferId, out var avatarEntry))
        {
            Post(() => AvatarArrived?.Invoke(avatarEntry.DeviceId, avatarEntry.Key, bytes));
            return;
        }

        if (_pendingNotificationImages.Remove(transferId, out var imgEntry))
        {
            Post(() => TopImageArrived?.Invoke(imgEntry.DeviceId, imgEntry.Key, bytes));
            return;
        }

        if (_pendingMessageImages.Remove(transferId, out var msgEntry))
        {
            Post(() => MessageImageArrived?.Invoke(msgEntry.DeviceId, msgEntry.Key, transferId, msgEntry.MimeType, bytes));
            return;
        }

        if (_pendingIconTransfers.Remove(transferId, out var iconEntry))
        {
            _iconsByPackage[iconEntry.PackageName] = bytes;
            _iconCache.Save(iconEntry.PackageName, bytes);
            Post(() => IconArrived?.Invoke(iconEntry.PackageName, bytes));
            return;
        }

        if (_pendingWallpaper.Remove(transferId, out var wallpaperDeviceId))
        {
            Post(() => WallpaperArrived?.Invoke(wallpaperDeviceId, bytes));
            return;
        }

        if (_pendingFiles.Remove(transferId, out var fileEntry))
        {
            HandleIncomingFile(fileEntry.DeviceId, fileEntry.FileName, fileEntry.MimeType, bytes);
            return;
        }

        if (_pendingMediaArt.Remove(transferId, out var mediaDeviceId))
        {
            MediaPlaybackState? updated = null;
            lock (_lastMediaState)
            {
                if (_lastMediaState.TryGetValue(mediaDeviceId, out var existing))
                {
                    updated = existing with { AlbumArtBytes = bytes };
                    _lastMediaState[mediaDeviceId] = updated;
                }
            }
            if (updated != null) Post(() => MediaStateChanged?.Invoke(mediaDeviceId, updated));
        }
    }

    /// <summary>Phone->PC half of file sharing (see docs/PROTOCOL.md's file.send) — saved to
    /// the real Windows Downloads folder, then rendered as a synthetic card exactly like an
    /// incoming text share (own icon, same list), so both sharing paths feel like the same
    /// feature. Images also populate ImageBytes so the card shows an actual thumbnail.</summary>
    private void HandleIncomingFile(string deviceId, string fileName, string mimeType, byte[] bytes)
    {
        var savedPath = SaveToDownloads(fileName, bytes);
        var isImage = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var key = $"{SharedCardKeyPrefix}{Guid.NewGuid()}";
        if (savedPath != null) _sharedFilePaths[key] = savedPath;

        var item = new NotificationItem
        {
            Key = key,
            PackageName = "com.notilink.app",
            AppLabel = "NotiLink",
            Title = "Archivo recibido desde el celular",
            Text = savedPath != null ? $"{fileName} — guardado en Descargas" : fileName,
            PostedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsClearable = true,
            IconBytes = AppInfo.IconBytes,
            HasSavedFile = savedPath != null,
        };
        Post(() => NotificationUpserted?.Invoke(deviceId, item));
        // A real notification.posted's image arrives the same two-step way: the card renders
        // first from the JSON payload alone, then TopImageArrived patches in the picture once
        // its bytes are in hand — reused here rather than adding a second image path through
        // JsNotificationMapper, which currently ignores NotificationItem.ImageBytes entirely.
        if (isImage) Post(() => TopImageArrived?.Invoke(deviceId, key, bytes));
    }

    /// <summary>Writes to the real Windows Downloads folder (not just a NotiLink-owned
    /// subfolder) per the user's request — appends " (2)", " (3)", etc. rather than
    /// overwriting if a file with the same name already landed there. Returns null (and skips
    /// the notification's "guardado en Descargas" line) if the write itself fails for any
    /// reason — the card still appears either way, just without that detail.</summary>
    private static string? SaveToDownloads(string fileName, byte[] bytes)
    {
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var safeName = string.Concat(fileName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            if (safeName.Length == 0) safeName = "archivo";
            var path = Path.Combine(downloads, safeName);
            var baseName = Path.GetFileNameWithoutExtension(safeName);
            var ext = Path.GetExtension(safeName);
            var n = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(downloads, $"{baseName} ({n}){ext}");
                n++;
            }
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Abrir archivo" on a received-file card — launches it with whatever the OS's own
    /// default handler for that file type is, same as double-clicking it in Explorer.</summary>
    /// <summary>Real absolute path lookup for a received-file card — used by MainWindow to start
    /// a native OS drag-and-drop straight out of the card (onto Explorer, another app, ...) via
    /// DragDrop.DoDragDrop, reusing the exact same file OpenSharedFile/OpenSharedFileLocation
    /// already know about. Null if the key isn't a received-file card, or the file's gone.</summary>
    public string? GetSharedFilePath(string key) =>
        _sharedFilePaths.TryGetValue(key, out var path) && File.Exists(path) ? path : null;

    /// <summary>"Eliminar archivo" on a received-file card — deletes the real file from Downloads
    /// immediately, no confirmation prompt (explicit request). Previously there was no way at all
    /// to get rid of a received file once saved: the card's own "×" only ever hid the card, never
    /// touched the file itself. JS removes the card optimistically right after sending this, same
    /// as every other delete in the app — this just needs to make the file itself actually gone.</summary>
    public void DeleteSharedFile(string key)
    {
        if (_sharedFilePaths.TryGetValue(key, out var path))
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* best-effort — nothing else to fall back to */ }
            _sharedFilePaths.Remove(key);
        }
    }

    public void OpenSharedFile(string key)
    {
        if (!_sharedFilePaths.TryGetValue(key, out var path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort — nothing else to fall back to if the OS itself can't open it.
        }
    }

    /// <summary>"Abrir ruta" on a received-file card — opens Explorer with the file itself
    /// highlighted (Explorer's own standard "/select," convention), not just the bare folder.</summary>
    public void OpenSharedFileLocation(string key)
    {
        if (!_sharedFilePaths.TryGetValue(key, out var path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort — nothing else to fall back to if Explorer itself can't be launched.
        }
    }

    /// <summary>PC->phone half of file sharing (drag & drop onto the window, confirmed via
    /// the page's own file-drop preview modal — see app.js's dragover/drop handlers) — the
    /// phone saves it to its own Downloads folder and posts a native notification, same as
    /// the text/link share. Takes raw bytes rather than a file path: the page reads the
    /// dropped file itself (standard HTML5 File API inside the WebView2-hosted page) and
    /// hands the bytes over already decoded from the data: URL it used for the preview.</summary>
    public Task SendFileToDevice(string deviceId, string fileName, string mimeType, byte[] bytes) =>
        _server.SendWithBinaryAsync(deviceId, "file.send", new { fileName, mimeType, size = bytes.Length, transferId = Guid.NewGuid().ToString() }, bytes);

    private byte[]? GetIconBytes(string packageName)
    {
        if (_iconsByPackage.TryGetValue(packageName, out var cached)) return cached;
        var fromDisk = _iconCache.TryGet(packageName);
        if (fromDisk != null) _iconsByPackage[packageName] = fromDisk;
        return fromDisk;
    }

    private static void Post(Action action) =>
        System.Windows.Application.Current.Dispatcher.Invoke(action);
}
