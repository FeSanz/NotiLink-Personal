using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using NotiLink.App.Interop;
using NotiLink.App.Services;
using NotiLink.App.ViewModels;
using NotiLink.Core.Networking;
using NotiLink.Core.Storage;

namespace NotiLink.App;

/// <summary>
/// The entire UI — notification list, status header, theme toggle — lives in
/// wwwroot/*.js inside a WebView2 control, so it all shares one theming
/// system (CSS) instead of native WPF chrome that used to fall out of sync
/// with it. This class is just the bridge: MainViewModel's events become JS
/// calls, and the page's button clicks arrive back here via postMessage.
/// Every per-phone call carries a deviceId so the page can keep each paired
/// device's notifications/battery/apps separate (see MainViewModel).
/// </summary>
public partial class MainWindow : Window
{
    private readonly TcpServer _server = new();
    private readonly PeerLinkService _peerLink;
    private readonly ScreenCaptureService _screenCapture;
    // pcId -> the window showing that PC's screen. Frames are pushed from the socket thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PeerScreenWindow> _screenWindows = new();
    // What "show the window" restores a minimized/hidden window to — never forces Normal, which
    // would un-maximize (and so move) a maximized window.
    private WindowState _lastShownState = WindowState.Normal;
    private readonly MainViewModel _viewModel;
    private readonly Services.HistoryStore _history = new();
    private readonly WindowPlacementStore _windowPlacementStore = new();
    private bool _webViewReady;
    private readonly List<string> _pendingScripts = new();
    private bool _restoreMaximized;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayBaseIcon;
    private System.Drawing.Icon? _taskbarOverlayIcon;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();
        // Joining another network (e.g. the phone's hotspot) changes this PC's IP — keep the
        // QR / IP shown in "Vincular dispositivo" current.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => { if (_webViewReady) PushNetworkInfo(); _viewModel.BroadcastPcAddresses(); });

        _viewModel = new MainViewModel(_server);
        _viewModel.DeviceListChanged += devices => RunJs($"window.setDeviceList({JsNotificationMapper.ToDeviceListJson(devices)})");
        _viewModel.NotificationUpserted += (deviceId, item) =>
            RunJs($"window.upsertNotification({JsonString(deviceId)}, {JsNotificationMapper.ToJson(item)})");
        _viewModel.NotificationRemoved += (deviceId, key) =>
            RunJs($"window.removeNotification({JsonString(deviceId)}, {JsonString(key)})");
        _viewModel.NotificationSyncReceived += (deviceId, keys) =>
            RunJs($"window.syncNotificationKeys({JsonString(deviceId)}, {JsonSerializer.Serialize(keys, LinkPreviewJsonOptions)})");
        _viewModel.IconArrived += (pkg, bytes) =>
            RunJs($"window.setIcon({JsonString(pkg)}, {JsonString(JsNotificationMapper.ToDataUrl(bytes, "image/png"))})");
        _viewModel.AppListReceived += (deviceId, apps) =>
            RunJs($"window.setAppList({JsonString(deviceId)}, {JsNotificationMapper.ToAppListJson(apps)})");
        _viewModel.AppFilterEntryChanged += (deviceId, pkg, allowed) =>
            RunJs($"window.setAppAllowedState({JsonString(deviceId)}, {JsonString(pkg)}, {(allowed ? "true" : "false")})");
        _viewModel.AvatarArrived += (deviceId, key, bytes) =>
            RunJs($"window.setAvatar({JsonString(deviceId)}, {JsonString(key)}, {JsonString(JsNotificationMapper.ToDataUrl(bytes, "image/jpeg"))})");
        _viewModel.TopImageArrived += (deviceId, key, bytes) =>
            RunJs($"window.setImage({JsonString(deviceId)}, {JsonString(key)}, {JsonString(JsNotificationMapper.ToDataUrl(bytes, "image/jpeg"))})");
        _viewModel.MessageImageArrived += (deviceId, key, transferId, mimeType, bytes) =>
            RunJs($"window.setMessageImage({JsonString(deviceId)}, {JsonString(key)}, {JsonString(transferId)}, {JsonString(JsNotificationMapper.ToDataUrl(bytes, mimeType))})");
        _viewModel.BatteryChanged += (deviceId, level, charging) =>
            RunJs($"window.setBattery({JsonString(deviceId)}, {level}, {(charging ? "true" : "false")})");
        _viewModel.PairingCodeShown += code => RunJs($"window.showPairingCode({JsonString(code)})");
        _viewModel.DevicePaired += (deviceId, name) => RunJs($"window.showPaired({JsonString(deviceId)}, {JsonString(name)})");
        _viewModel.DeviceReconnected += (deviceId, name) => RunJs($"window.showDeviceReconnected({JsonString(deviceId)}, {JsonString(name)})");
        _viewModel.WallpaperArrived += (deviceId, bytes) =>
            RunJs($"window.setWallpaper({JsonString(deviceId)}, {JsonString(JsNotificationMapper.ToDataUrl(bytes, "image/jpeg"))})");
        _viewModel.WifiChanged += (deviceId, ssid, level) =>
            RunJs($"window.setWifi({JsonString(deviceId)}, {JsonString(ssid)}, {(level.HasValue ? level.Value.ToString() : "null")})");
        _viewModel.CellularChanged += (deviceId, lines) =>
            RunJs($"window.setCellular({JsonString(deviceId)}, {JsonSerializer.Serialize(lines, LinkPreviewJsonOptions)})");
        _viewModel.VolumeChanged += (deviceId, level, max) =>
            RunJs($"window.setVolume({JsonString(deviceId)}, {level}, {max})");
        _viewModel.RotationChanged += (deviceId, active) =>
            RunJs($"window.setRotation({JsonString(deviceId)}, {(active ? "true" : "false")})");
        _viewModel.DndChanged += (deviceId, active) =>
            RunJs($"window.setDnd({JsonString(deviceId)}, {(active ? "true" : "false")})");
        _viewModel.MediaStateChanged += (deviceId, state) =>
        {
            if (state == null)
            {
                RunJs($"window.setMediaState({JsonString(deviceId)}, null)");
                return;
            }
            var dto = new
            {
                appLabel = state.AppLabel,
                title = state.Title,
                artist = state.Artist,
                album = state.Album,
                isPlaying = state.IsPlaying,
                position = state.Position,
                duration = state.Duration,
                canPlayPause = state.CanPlayPause,
                canNext = state.CanNext,
                canPrevious = state.CanPrevious,
                canFavorite = state.CanFavorite,
                isFavorite = state.IsFavorite,
                canShuffle = state.CanShuffle,
                isShuffleOn = state.IsShuffleOn,
                isSmartShuffle = state.IsSmartShuffle,
                canSeek = state.CanSeek,
                albumArtDataUrl = JsNotificationMapper.ToDataUrl(state.AlbumArtBytes, "image/jpeg"),
            };
            RunJs($"window.setMediaState({JsonString(deviceId)}, {JsonSerializer.Serialize(dto, LinkPreviewJsonOptions)})");
        };
        _viewModel.PcMediaStateChanged += state =>
        {
            if (state == null)
            {
                RunJs("window.setPcMediaState(null)");
                return;
            }
            var dto = new
            {
                appLabel = state.AppLabel,
                title = state.Title,
                artist = state.Artist,
                album = state.Album,
                isPlaying = state.IsPlaying,
                position = state.Position,
                duration = state.Duration,
                canPlayPause = state.CanPlayPause,
                canNext = state.CanNext,
                canPrevious = state.CanPrevious,
                canSeek = state.CanSeek,
                canShuffle = state.CanShuffle,
                isShuffleOn = state.IsShuffleOn,
                albumArtDataUrl = JsNotificationMapper.ToDataUrl(state.AlbumArtBytes, "image/jpeg"),
            };
            RunJs($"window.setPcMediaState({JsonSerializer.Serialize(dto, LinkPreviewJsonOptions)})");
        };
        _viewModel.ScreenShareStatsChanged += (deviceId, stats) =>
        {
            var fpsJs = stats.Fps.HasValue ? stats.Fps.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
            RunJs($"window.setScreenShareStats({JsonString(deviceId)}, {fpsJs}, {JsonString(stats.Resolution)})");
        };
        _viewModel.ScreenShareStopped += deviceId => RunJs($"window.setScreenShareStopped({JsonString(deviceId)})");
        _viewModel.AudioLevelChanged += (deviceId, level) =>
            RunJs($"window.setAudioLevel({JsonString(deviceId)}, {level.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        _viewModel.AudioLevelUnavailable += deviceId => RunJs($"window.setAudioLevelUnavailable({JsonString(deviceId)})");
        _viewModel.AppShareStateChanged += (deviceId, pkg, state) =>
            RunJs($"window.setAppShareState({JsonString(deviceId)}, {JsonString(pkg)}, {JsonString(state)})");
        _viewModel.AppShareStopped += (deviceId, pkg) => RunJs($"window.setAppShareStopped({JsonString(deviceId)}, {JsonString(pkg)})");
        _viewModel.AudioShareStopped += deviceId => RunJs($"window.setAudioShareStopped({JsonString(deviceId)})");
        // The phone asked to start sharing (see docs/PROTOCOL.md's share.request) — bounced into
        // JS rather than handled here, since the screen-share quality settings the user asked to
        // reuse ("la configuración guardada en el pc") live in the page's own localStorage, not
        // anywhere on the C# side.
        // Waves "al ritmo de la música": band levels of what this PC plays (loopback) and of what
        // the phone plays (media.levels), pushed to the now-playing bar while it asks for them.
        _spectrum.Levels += (b, m, t) => Dispatcher.BeginInvoke(() =>
            RunJs($"window.setAudioLevels && window.setAudioLevels('pc', {F(b)}, {F(m)}, {F(t)})"));
        _viewModel.MediaLevels += (deviceId, b, m, t) =>
            RunJs($"window.setAudioLevels && window.setAudioLevels('phone', {F(b)}, {F(m)}, {F(t)}, {JsonString(deviceId)})");
        _viewModel.PhoneNetworkChanged += (deviceId, metered, transport, vpn, route) =>
            RunJs($"window.setPhoneNetwork({JsonString(deviceId)}, {JsonSerializer.Serialize(new { metered, transport, vpn, route })})");
        _viewModel.ShareRecentsReceived += (deviceId, appsJson, updatedAt) =>
            RunJs($"window.onPhoneShareRecents({JsonString(deviceId)}, {appsJson}, {updatedAt})");
        _viewModel.SharePhoneRequested += (deviceId, type, pkg, label) =>
            RunJs($"window.onPhoneShareRequest({JsonString(deviceId)}, {JsonString(type)}, {JsonString(pkg ?? "")}, {JsonString(label ?? "")})");
        // Telecom-session-based call detection (see docs/PROTOCOL.md's call.state) — covers both
        // native and VoIP calls that never post a notification while ringing, unlike the existing
        // CallStyle-notification toast in upsertNotification which only fires when a notification
        // actually arrives. Can't tell incoming from outgoing, see call.state's own doc comment.
        _viewModel.CallActive += (deviceId, deviceName) => RunJs($"window.onCallActive({JsonString(deviceId)}, {JsonString(deviceName)})");
        _viewModel.CallEnded += (deviceId) => RunJs($"window.onCallEnded({JsonString(deviceId)})");
        _viewModel.CallStatusChanged += (deviceId, answered, elapsed, speaker, muted) =>
            RunJs($"window.onCallStatus({JsonString(deviceId)}, {(answered ? "true" : "false")}, {(elapsed.HasValue ? elapsed.Value.ToString() : "null")}, {(speaker ? "true" : "false")}, {(muted ? "true" : "false")})");
        _viewModel.CallControlResult += (success, action, method) =>
            RunJs($"window.setCallControlResult({(success ? "true" : "false")}, {JsonString(action)}, {JsonString(method)})");
        _viewModel.OpenNotificationResult += (success, reason) =>
            RunJs($"window.setOpenNotificationResult({(success ? "true" : "false")}, {(reason == null ? "null" : JsonString(reason))})");

        // Maximizing is deferred to Loaded (never set in the constructor) — WPF only
        // computes RestoreBounds correctly once the window has a real HWND at its Normal
        // size; maximizing before that corrupts RestoreBounds to a tiny, unrelated rect,
        // which then gets persisted as the "un-maximize to" size on the next Closing.
        Loaded += (_, _) => { if (_restoreMaximized) WindowState = WindowState.Maximized; };
        Loaded += async (_, _) => await InitializeWebViewAsync();

        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastShownState = WindowState; };

        _peerLink = new PeerLinkService(() => Services.NetworkInfo.GetAddressesForPhone().Select(a => a.Ip).ToList());
        _screenCapture = new ScreenCaptureService(_peerLink);
        WirePeerLink();
        _server.PeerHandoff = _peerLink.AcceptIncoming;

        _server.Start();
        _viewModel.StartDiscovery(_peerLink.MyPcId);
        _peerLink.Start();
        InitializeTrayIcon();

        // Closing the window (the X button, Alt+F4) behaves like minimizing — the app keeps
        // running in the background and mirroring notifications exactly as if it were open,
        // same as Discord/Spotify/etc. Only the tray menu's "Salir" actually exits.
        Closing += (_, e) =>
        {
            SaveWindowPlacement();
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
            }
        };
        // Windows shutting down / restarting / signing out, and the PC going to sleep — the phone
        // gets a goodbye so it stops showing its NotiLink notifications (see SayGoodbye).
        System.Windows.Application.Current.SessionEnding += (_, e) =>
            _viewModel.SayGoodbye(e.ReasonSessionEnding == ReasonSessionEnding.Shutdown ? "shutdown" : "logoff");
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) _viewModel.SayGoodbye("sleep");
        };

        Closed += (_, _) =>
        {
            _spectrum.Dispose();
            _viewModel.SayGoodbye("exit");
            _screenCapture.Dispose();
            foreach (var w in _screenWindows.Values) w.CloseFromPeer();
            _peerLink.Dispose();
            _trayIcon?.Dispose();
            _taskbarOverlayIcon?.Dispose();
            _trayBaseIcon?.Dispose();
            _server.Dispose();
            _viewModel.Shutdown();
            _history.Flush();
        };
    }

    /// <summary>PC ↔ PC link (see PeerLinkService): its events arrive on background threads, so
    /// each one hops onto the dispatcher before touching the page, the clipboard or the tray.</summary>
    private void WirePeerLink()
    {
        void Ui(Action a) => Dispatcher.BeginInvoke(a);
        _peerLink.Log += PcDiagLog.Write;
        _peerLink.PeersChanged += () => Ui(PushPeers);
        _peerLink.ChatItemChanged += (pcId, item) => Ui(() =>
        {
            RunJs($"window.onPeerChatItem({JsonString(pcId)}, {PeerChatStore.ToJson(item)})");
            // Only a brand-new incoming message/file deserves a tray balloon, not its progress ticks.
            if (!item.Outgoing && !IsVisible && item.State is null or "receiving" && item.Transferred == 0)
            {
                var from = _peerLink.ListPeers().FirstOrDefault(p => p.PcId == pcId)?.Name ?? "Otra PC";
                var text = item.Kind switch
                {
                    "file" => $"📄 {item.FileName}",
                    "clipboard" => "📋 Portapapeles recibido (ya está copiado)",
                    "nudge" => "👋 Te está llamando la atención",
                    _ => item.Text ?? "",
                };
                _trayIcon?.ShowBalloonTip(4000, from, text.Length > 200 ? text[..200] + "…" : text, System.Windows.Forms.ToolTipIcon.None);
            }
        });
        _peerLink.PairCodeShown += (name, code) => Ui(() =>
        {
            RunJs($"window.onPeerPairCode({JsonString(name)}, {JsonString(code)})");
            _trayIcon?.ShowBalloonTip(15000, "Vincular PC", $"{name} quiere vincularse. Código: {code}", System.Windows.Forms.ToolTipIcon.Info);
        });
        _peerLink.PairCodeDismissed += () => Ui(() => RunJs("window.onPeerPairCodeDismissed()"));
        _peerLink.PairCodeNeeded += name => Ui(() => RunJs($"window.onPeerPairCodeNeeded({JsonString(name)})"));
        _peerLink.PairResult += (ok, message) => Ui(() => RunJs($"window.onPeerPairResult({(ok ? "true" : "false")}, {JsonString(message)})"));
        _peerLink.ClipboardReceived += (_, text) => Ui(() =>
        {
            try { System.Windows.Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard busy — the text is still in the chat */ }
        });
        _peerLink.NudgeReceived += (pcId, name) => Ui(() =>
        {
            System.Media.SystemSounds.Exclamation.Play();
            ShowFromTray();
            FlashTaskbar();
            RunJs($"window.onPeerNudge({JsonString(pcId)}, {JsonString(name)})");
        });

        // ---- screen sharing (see PeerLinkService's "screen sharing" section)
        string PeerName(string pcId) => _peerLink.ListPeers().FirstOrDefault(p => p.PcId == pcId)?.Name ?? "Otra PC";
        void Notice(string text) => RunJs($"window.onPeerScreenNotice({JsonString(text)})");
        _peerLink.ScreenRequested += (pcId, name) => Ui(() =>
        {
            ShowFromTray();
            FlashTaskbar();
            System.Media.SystemSounds.Asterisk.Play();
            RunJs($"window.onPeerScreenRequest({JsonString(pcId)}, {JsonString(name)})");
        });
        _peerLink.ScreenSharingChanged += (pcId, on) => Ui(() =>
        {
            _screenCapture.Update();
            var name = PeerName(pcId);
            Notice(on ? $"🔴 {name} está viendo tu pantalla" : $"Dejaste de compartir tu pantalla con {name}");
            if (on && !IsVisible)
                _trayIcon?.ShowBalloonTip(5000, "Compartiendo pantalla", $"{name} está viendo tu pantalla. Ábrelo para detenerlo.", System.Windows.Forms.ToolTipIcon.Warning);
        });
        _peerLink.ScreenViewStarted += (pcId, name) => Ui(() =>
        {
            if (_screenWindows.TryGetValue(pcId, out var existing)) { existing.Activate(); return; }
            var window = new PeerScreenWindow(name,
                onUserClose: () =>
                {
                    _screenWindows.TryRemove(pcId, out _);
                    _peerLink.StopViewingScreen(pcId);
                },
                onControlToggle: async wantControl =>
                {
                    if (!wantControl) { _peerLink.StopControl(pcId); return; }
                    if (!await _peerLink.RequestControlAsync(pcId)) Ui(() => { if (_screenWindows.TryGetValue(pcId, out var w)) w.ControlDenied(); });
                    else Notice($"🖱️ Pidiendo control de {name}… (si no te dio permiso permanente, tiene que aceptar ahí)");
                },
                sendInput: input => _peerLink.SendInput(pcId, input));
            _screenWindows[pcId] = window;
            window.Show();
            window.Activate();
        });
        _peerLink.ScreenFrameReceived += (pcId, jpeg) =>
        {
            if (_screenWindows.TryGetValue(pcId, out var window)) window.PushFrame(jpeg);
        };
        _peerLink.ScreenViewEnded += (pcId, reason) => Ui(() =>
        {
            if (_screenWindows.TryRemove(pcId, out var window)) window.CloseFromPeer();
            var name = PeerName(pcId);
            switch (reason)
            {
                case "denied": Notice($"⚠️ {name} no aceptó compartir su pantalla"); break;
                case "stopped": Notice($"{name} dejó de compartir su pantalla"); break;
                case "disconnected": Notice($"⚠️ Se perdió la conexión con {name}"); break;
            }
        });

        // ---- remote control on top of a screen share
        _screenCapture.Log += PcDiagLog.Write;
        _peerLink.ControlRequested += (pcId, name) => Ui(() =>
        {
            ShowFromTray();
            FlashTaskbar();
            System.Media.SystemSounds.Asterisk.Play();
            RunJs($"window.onPeerControlRequest({JsonString(pcId)}, {JsonString(name)})");
        });
        _peerLink.ControlledByChanged += (pcId, on) =>
        {
            if (!on) InputInjector.ReleaseAll();
            Ui(() =>
            {
                var name = PeerName(pcId);
                Notice(on ? $"🖱️ {name} está controlando tu PC" : $"{name} dejó de controlar tu PC");
                if (on && !IsVisible)
                    _trayIcon?.ShowBalloonTip(5000, "Control remoto", $"{name} está controlando tu PC. Ábrelo para detenerlo.", System.Windows.Forms.ToolTipIcon.Warning);
            });
        };
        // Injected straight from the socket thread — SendInput doesn't need the UI thread, and
        // hopping through the dispatcher would only add latency to every mouse move.
        _peerLink.InputReceived += (_, input) => InputInjector.Inject(input);
        _peerLink.ControllingChanged += (pcId, on) => Ui(() =>
        {
            if (_screenWindows.TryGetValue(pcId, out var window)) window.SetControlling(on);
            Notice(on ? $"🖱️ Estás controlando {PeerName(pcId)} (Ctrl+Alt+Q para soltar)" : $"Dejaste de controlar {PeerName(pcId)}");
        });
        _peerLink.ControlDenied += pcId => Ui(() =>
        {
            if (_screenWindows.TryGetValue(pcId, out var window)) window.ControlDenied();
            Notice($"⚠️ {PeerName(pcId)} no aceptó que la controles");
        });
    }

    private void PushPeers()
    {
        var peers = _peerLink.ListPeers();
        var self = new
        {
            pcId = _peerLink.MyPcId,
            name = PeerLinkService.MyName,
            addresses = Services.NetworkInfo.GetAddressesForPhone().Select(a => new { ip = a.Ip, kind = a.Kind }).ToList(),
        };
        RunJs($"window.setPeers({JsonSerializer.Serialize(peers, LinkPreviewJsonOptions)}, {JsonSerializer.Serialize(self, LinkPreviewJsonOptions)})");
    }

    private async Task HandlePeerMessage(string action, JsonElement root, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var pcId = S("pcId");
        switch (action)
        {
            case "peerList":
                PushPeers();
                break;
            case "peerHistory":
                RunJs($"window.setPeerChat({JsonString(pcId)}, {PeerChatStore.ToJson(_peerLink.History(pcId))})");
                break;
            case "peerDismiss":
                _peerLink.DismissChatItems(pcId, S("id") is { Length: > 0 } id ? id : null);
                break;
            case "peerClearHistory":
                _peerLink.ClearHistory(pcId);
                RunJs($"window.setPeerChat({JsonString(pcId)}, [])");
                break;
            case "peerScan":
            {
                var found = await _peerLink.ScanAsync();
                RunJs($"window.setPeerScanResults({JsonSerializer.Serialize(found, LinkPreviewJsonOptions)})");
                break;
            }
            case "peerPairStart":
                await _peerLink.BeginPairAsync(S("ip"));
                break;
            case "peerPairCode":
                await _peerLink.SubmitPairCodeAsync(S("code"));
                break;
            case "peerPairCancel":
                _peerLink.CancelPairing();
                break;
            case "peerPairReject":
                _peerLink.RejectIncomingPairing();
                break;
            case "peerSend":
                if (!await _peerLink.SendTextAsync(pcId, S("text"))) RunJs($"window.onPeerSendFailed({JsonString(pcId)})");
                break;
            case "peerSendClipboard":
            {
                string clip = "";
                try { if (System.Windows.Clipboard.ContainsText()) clip = System.Windows.Clipboard.GetText(); }
                catch (System.Runtime.InteropServices.COMException) { }
                if (clip.Length == 0) { RunJs("window.onPeerClipboardEmpty()"); break; }
                if (!await _peerLink.SendTextAsync(pcId, clip, "clipboard")) RunJs($"window.onPeerSendFailed({JsonString(pcId)})");
                break;
            }
            case "peerNudge":
                if (!await _peerLink.NudgeAsync(pcId)) RunJs($"window.onPeerSendFailed({JsonString(pcId)})");
                break;
            case "peerSendFiles":
            {
                // Real paths of the File objects the page picked/dropped (postMessageWithAdditionalObjects),
                // so files of any size stream from disk instead of passing through the page as base64.
                var paths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? new();
                if (paths.Count == 0) break;
                _ = Task.Run(async () =>
                {
                    if (!await _peerLink.SendFilesAsync(pcId, paths))
                        _ = Dispatcher.BeginInvoke(() => RunJs($"window.onPeerSendFailed({JsonString(pcId)})"));
                });
                break;
            }
            case "peerCancelTransfer":
                _peerLink.CancelTransfer(pcId, S("transferId"));
                break;
            case "peerOpenFile":
            case "peerShowFile":
            {
                var path = _peerLink.FindChatItem(pcId, S("id"))?.Path;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { RunJs("window.onPeerFileMissing()"); break; }
                try
                {
                    Process.Start(action == "peerOpenFile"
                        ? new ProcessStartInfo(path) { UseShellExecute = true }
                        : new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                catch (Exception ex) { Debug.WriteLine($"peer file open failed: {ex.Message}"); }
                break;
            }
            case "peerScreenView":
                if (!await _peerLink.RequestScreenAsync(pcId)) RunJs($"window.onPeerSendFailed({JsonString(pcId)})");
                break;
            case "peerScreenShare":
                if (!await _peerLink.ShareScreenAsync(pcId)) RunJs($"window.onPeerSendFailed({JsonString(pcId)})");
                break;
            case "peerScreenStop":
                _peerLink.StopSharingScreen(pcId);
                break;
            case "peerScreenAnswer":
            {
                bool B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                _peerLink.AnswerScreenRequest(pcId, B("allow"), B("always"));
                break;
            }
            case "peerControlAnswer":
            {
                bool B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                _peerLink.AnswerControlRequest(pcId, B("allow"), B("always"));
                break;
            }
            case "peerControlStop":
                _peerLink.StopControl(pcId);
                break;
            case "peerControlAllow":
                _peerLink.SetAllowControl(pcId, root.TryGetProperty("allow", out var allowCtl) && allowCtl.ValueKind == JsonValueKind.True);
                break;
            case "peerScreenAllow":
                _peerLink.SetAllowScreen(pcId, root.TryGetProperty("allow", out var allowEl) && allowEl.ValueKind == JsonValueKind.True);
                break;
            case "peerRename":
                _peerLink.Rename(pcId, S("name"));
                break;
            case "peerUnpair":
                await _peerLink.UnpairAsync(pcId);
                break;
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            // Assembly.Location is empty for a single-file published build (see IL3000) —
            // Environment.ProcessPath reliably gives the actual running exe's path either way.
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                _trayBaseIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch (Exception)
        {
            // Falls back to no icon (still functional, just visually blank in the tray) rather
            // than failing the whole app over a cosmetic detail.
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        menu.Items.Add("Salir", null, (_, _) => ExitFromTray());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayBaseIcon,
            Text = "NotiLink",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    /// <summary>Updates the notification-count badge — primarily the real taskbar button's
    /// overlay badge (ITaskbarList3, the same mechanism Teams/Slack/Discord use, visible
    /// above the app's actual taskbar icon), plus the tray icon's tooltip text as a secondary
    /// hint for when the window is hidden and has no taskbar button at all. Driven by
    /// app.js's notifyCountChanged, the single source of truth for "how many notifications
    /// are currently in the list".</summary>
    private void PushNetworkInfo()
    {
        var ips = Services.NetworkInfo.GetLocalIPv4s();
        var best = ips.Count > 0 ? ips[0].Ip : _viewModel.LocalIp;
        var others = ips.Skip(1).Select(x => new { ip = x.Ip, adapter = x.Adapter }).ToList();
        RunJs($"window.setQrCode({JsonString(Services.QrCodeGenerator.ToDataUrl(best))}, {JsonString(best)}, {JsonSerializer.Serialize(others)})");
    }

    private void UpdateTrayBadge(int count)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var previous = _taskbarOverlayIcon;
            _taskbarOverlayIcon = count > 0 ? ComposeOverlayBadgeIcon(count) : null;
            if (_taskbarOverlayIcon != null)
                Interop.TaskbarBadge.SetOverlay(hwnd, _taskbarOverlayIcon.Handle, $"{count} notificaciones");
            else
                Interop.TaskbarBadge.ClearOverlay(hwnd);
            previous?.Dispose();
        }

        if (_trayIcon != null)
            _trayIcon.Text = count > 0 ? $"NotiLink — {count} notificaci{(count == 1 ? "ón" : "ones")}" : "NotiLink";
    }

    /// <summary>Just the circle + count — no base app icon underneath, since
    /// ITaskbarList3.SetOverlayIcon already composites this onto the corner of the real
    /// taskbar icon on its own. Same solid, bold shape/weight as WhatsApp Desktop's own
    /// taskbar badge (solid fill, white number, no outline) — just NotiLink's brand purple
    /// instead of WhatsApp's green, at a comparably saturated intensity, not washed out.</summary>
    private static System.Drawing.Icon ComposeOverlayBadgeIcon(int count)
    {
        const int size = 32;
        using var bitmap = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var label = count > 9 ? "9+" : count.ToString();
            var badgeRect = new System.Drawing.Rectangle(1, 1, size - 2, size - 2);

            using var badgeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 124, 92, 255));
            g.FillEllipse(badgeBrush, badgeRect);

            using var font = new System.Drawing.Font("Segoe UI", label.Length > 1 ? 12f : 15f, System.Drawing.FontStyle.Bold);
            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Center,
                LineAlignment = System.Drawing.StringAlignment.Center,
            };
            g.DrawString(label, font, textBrush, badgeRect, format);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            // Icon.FromHandle() doesn't take ownership of the handle — Clone() copies the bits
            // into a GDI+-managed Icon, after which the raw HICON must be destroyed manually.
            Interop.IconApi.Destroy(hIcon);
        }
    }

    /// <summary>Brings the window back without touching its size or position: hidden → shown,
    /// minimized → whatever it was before (Normal or Maximized). It used to force Normal, which
    /// un-maximized — and so moved — a maximized window every time, e.g. on every nudge.</summary>
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastShownState;
        Activate();
    }

    /// <summary>Blinks the taskbar button until the window is focused — in case Windows refused
    /// to bring it to the front (it only lets the foreground app steal focus).</summary>
    private void FlashTaskbar()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = 5,
        };
        FlashWindowEx(ref info);
    }

    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private void ExitFromTray()
    {
        _isExiting = true;
        Close();
    }

    /// <summary>Falls back to the XAML-defined default size/position if nothing was saved yet,
    /// or if the saved rect no longer overlaps any current monitor (e.g. an external display
    /// that was unplugged since the last run) — never restores to an unreachable position.</summary>
    private void RestoreWindowPlacement()
    {
        var placement = _windowPlacementStore.Load();
        // Below ~200px wide/tall it's not a real remembered size — almost certainly a
        // corrupted RestoreBounds capture from the maximize-in-constructor bug (see
        // Loaded handler below) — safer to fall back to the XAML default than restore it.
        if (placement == null || placement.Width < 200 || placement.Height < 200) return;

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var savedRect = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!virtualScreen.IntersectsWith(savedRect)) return;

        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        _restoreMaximized = placement.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var maximized = WindowState == WindowState.Maximized;
        // RestoreBounds is the un-maximized size/position even while currently maximized —
        // exactly what should be reapplied if the window is un-maximized next launch.
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _windowPlacementStore.Save(new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized));
    }

    private async Task InitializeWebViewAsync()
    {
        await Browser.EnsureCoreWebView2Async();
        // AllowExternalDrop defaults to true — this is what lets a real OS file drag from
        // Explorer reach the page's own drop event (see app.js's file-drop handler) as a
        // standard HTML5 DragEvent, instead of WebView2 swallowing/blocking it.

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.notilink.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        // Saved history images (see HistoryStore) — served read-only from their own folder.
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Services.HistoryStore.ImageHost, _history.ImageDir, CoreWebView2HostResourceAccessKind.Allow);

        Browser.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) return;
            _webViewReady = true;
            RunJs($"window.setDeviceList({JsNotificationMapper.ToDeviceListJson(_viewModel.GetPairedDevices())})");
            PushNetworkInfo();
            PushPeers();
            RunJs($"window.setAppInfo({JsonString(AppInfo.Version)}, {JsonString(AppInfo.BuildTimestamp)}, {JsonString(AppInfo.Author)})");
            foreach (var script in _pendingScripts)
                _ = Browser.CoreWebView2.ExecuteScriptAsync(script);
            _pendingScripts.Clear();
        };

        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        // Links in notification text render as target="_blank" anchors (see app.js
        // linkifyHtml) specifically so this fires instead of the WebView2 navigating
        // itself away from the app — hand the URL to Windows' own default handler.
        Browser.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open link '{e.Uri}': {ex.Message}");
            }
        };

        Browser.CoreWebView2.Navigate("https://app.notilink.local/index.html");
    }

    /// <summary>Handles every command the page sends via window.chrome.webview.postMessage:
    /// per-card delete, the notification's own action buttons, "delete all", the theme
    /// toggle, and renaming the phone label — each scoped to the deviceId the page sends.</summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();

            if (action != null && action.StartsWith("peer", StringComparison.Ordinal))
            {
                await HandlePeerMessage(action, root, e);
                return;
            }

            switch (action)
            {
                case "delete":
                    await _viewModel.DeleteNotification(root.GetProperty("deviceId").GetString() ?? "", root.GetProperty("key").GetString() ?? "");
                    break;
                case "openNotification":
                    await _viewModel.OpenNotification(root.GetProperty("deviceId").GetString() ?? "", root.GetProperty("key").GetString() ?? "");
                    break;
                case "openMedia":
                    await _viewModel.OpenMedia(root.GetProperty("deviceId").GetString() ?? "");
                    break;
                case "copyText":
                    var textToCopy = root.GetProperty("text").GetString() ?? "";
                    if (textToCopy.Length > 0) System.Windows.Clipboard.SetText(textToCopy);
                    break;
                case "openImage":
                    OpenImageInPhotos(root.GetProperty("src").GetString() ?? "");
                    break;
                case "openFile":
                    _viewModel.OpenSharedFile(root.GetProperty("key").GetString() ?? "");
                    break;
                case "openFilePath":
                    _viewModel.OpenSharedFileLocation(root.GetProperty("key").GetString() ?? "");
                    break;
                case "deleteFile":
                    _viewModel.DeleteSharedFile(root.GetProperty("key").GetString() ?? "");
                    break;
                case "invoke":
                    var deviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var key = root.GetProperty("key").GetString() ?? "";
                    var actionIndex = root.GetProperty("actionIndex").GetInt32();
                    var replyText = root.TryGetProperty("replyText", out var rt) && rt.ValueKind == JsonValueKind.String
                        ? rt.GetString()
                        : null;
                    await _viewModel.InvokeAction(deviceId, key, actionIndex, replyText);
                    break;
                case "deleteAll":
                    await _viewModel.DeleteAll(root.GetProperty("deviceId").GetString() ?? "");
                    break;
                case "themeChanged":
                    // The page owns theme state (persisted in its own localStorage) and applies
                    // it to itself immediately — this just keeps the native title bar in sync.
                    DwmApi.SetImmersiveDarkMode(new WindowInteropHelper(this).Handle, root.GetProperty("dark").GetBoolean());
                    break;
                case "setDeviceBadge":
                    var badgeDeviceId = root.GetProperty("deviceId").GetString();
                    if (!string.IsNullOrWhiteSpace(badgeDeviceId))
                        _viewModel.SetDeviceBadge(badgeDeviceId,
                            root.TryGetProperty("text", out var btEl) ? btEl.GetString() : null,
                            root.TryGetProperty("color", out var bcEl) ? bcEl.GetString() : null);
                    break;
                case "syncShareRecents":
                    var recentsDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    if (recentsDeviceId.Length > 0 && root.TryGetProperty("apps", out var recentsEl))
                        _ = _viewModel.SendShareRecents(recentsDeviceId, recentsEl.Clone(),
                            root.TryGetProperty("updatedAt", out var ruEl) && ruEl.ValueKind == JsonValueKind.Number ? (long)ruEl.GetDouble() : 0L);
                    break;
                case "renameDevice":
                    var renameDeviceId = root.GetProperty("deviceId").GetString();
                    var name = root.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(renameDeviceId) && !string.IsNullOrWhiteSpace(name))
                        _viewModel.RenameDevice(renameDeviceId, name);
                    break;
                case "setAppAllowed":
                    var appDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var appPackage = root.GetProperty("packageName").GetString() ?? "";
                    var allowed = root.GetProperty("allowed").GetBoolean();
                    if (appDeviceId.Length > 0 && appPackage.Length > 0) await _viewModel.SetAppAllowed(appDeviceId, appPackage, allowed);
                    break;
                case "toggleConnection":
                    var toggleDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var disconnect = root.GetProperty("disconnect").GetBoolean();
                    if (toggleDeviceId.Length > 0) await _viewModel.ToggleConnection(toggleDeviceId, disconnect);
                    break;
                case "removeDevice":
                    var removeDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    if (removeDeviceId.Length > 0) _viewModel.RemoveDevice(removeDeviceId);
                    break;
                case "ringDevice":
                    var ringDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    if (ringDeviceId.Length > 0) await _viewModel.RingDevice(ringDeviceId);
                    break;
                case "setVolume":
                    var volumeDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var volumeLevel = root.GetProperty("level").GetInt32();
                    if (volumeDeviceId.Length > 0) await _viewModel.SendVolume(volumeDeviceId, volumeLevel);
                    break;
                case "setRotation":
                    var rotDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var rotActive = root.GetProperty("active").GetBoolean();
                    if (rotDeviceId.Length > 0) await _viewModel.SendRotation(rotDeviceId, rotActive);
                    break;
                case "setDnd":
                    var dndDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var dndActive = root.GetProperty("active").GetBoolean();
                    if (dndDeviceId.Length > 0) await _viewModel.SendDnd(dndDeviceId, dndActive);
                    break;
                case "startFileDrag":
                    StartFileDrag(root.GetProperty("key").GetString() ?? "");
                    break;
                case "fetchLinkPreview":
                    var previewUrl = root.GetProperty("url").GetString() ?? "";
                    if (previewUrl.Length > 0) _ = HandleFetchLinkPreview(previewUrl);
                    break;
                case "notificationCountChanged":
                    UpdateTrayBadge(root.GetProperty("count").GetInt32());
                    break;
                case "shareToDevice":
                    var shareDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var shareText = root.GetProperty("text").GetString() ?? "";
                    if (shareDeviceId.Length > 0 && shareText.Length > 0) await _viewModel.ShareToDevice(shareDeviceId, shareText);
                    break;
                case "historyUpsert":
                    if (System.Text.Json.Nodes.JsonNode.Parse(root.GetProperty("entry").GetRawText()) is System.Text.Json.Nodes.JsonObject histEntry)
                        _history.Upsert(histEntry);
                    break;
                case "historyRemoved":
                    _history.MarkRemoved(root.GetProperty("id").GetString() ?? "",
                        root.GetProperty("reason").GetString() ?? "unknown", root.GetProperty("at").GetInt64());
                    break;
                case "historyList":
                    RunJs($"window.setHistoryEntries({_history.ListJson()}, {_history.StatsJson()})");
                    break;
                case "historyDelete":
                {
                    var freed = _history.Delete(root.GetProperty("id").GetString() ?? "");
                    RunJs($"window.onHistoryDeleted('one', 1, {freed}, {_history.StatsJson()})");
                    break;
                }
                case "historyDeleteOlder":
                {
                    var (count, freed) = _history.DeleteOlderThan(root.GetProperty("before").GetInt64());
                    RunJs($"window.setHistoryEntries({_history.ListJson()}, {_history.StatsJson()})");
                    RunJs($"window.onHistoryDeleted('older', {count}, {freed}, {_history.StatsJson()})");
                    break;
                }
                case "historyClear":
                {
                    var (count, freed) = _history.Clear();
                    RunJs($"window.onHistoryDeleted('all', {count}, {freed}, {_history.StatsJson()})");
                    break;
                }
                case "openLogsFolder":
                    OpenLogsFolder();
                    break;
                case "mediaControl":
                    var mediaDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var mediaAction = root.GetProperty("control").GetString() ?? "";
                    var mediaPosition = root.TryGetProperty("position", out var posEl) && posEl.ValueKind == JsonValueKind.Number
                        ? posEl.GetInt64()
                        : (long?)null;
                    if (mediaDeviceId.Length > 0 && mediaAction.Length > 0) await _viewModel.SendMediaControl(mediaDeviceId, mediaAction, mediaPosition);
                    break;
                case "pcMediaControl":
                    var pcMediaAction = root.GetProperty("control").GetString() ?? "";
                    var pcMediaPosition = root.TryGetProperty("position", out var pcPosEl) && pcPosEl.ValueKind == JsonValueKind.Number
                        ? pcPosEl.GetInt64()
                        : (long?)null;
                    if (pcMediaAction.Length > 0) _viewModel.SendPcMediaControl(pcMediaAction, pcMediaPosition);
                    break;
                case "shareScreen":
                    var shareScreenDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    if (shareScreenDeviceId.Length > 0)
                    {
                        var shareMaxSize = root.TryGetProperty("maxSize", out var maxSizeEl) ? maxSizeEl.GetInt32() : 0;
                        var shareMaxFps = root.TryGetProperty("maxFps", out var maxFpsEl) ? maxFpsEl.GetInt32() : 60;
                        var shareBitRate = root.TryGetProperty("bitRate", out var bitRateEl) ? bitRateEl.GetInt32() : 20;
                        var shareCodec = root.TryGetProperty("videoCodec", out var codecEl) ? codecEl.GetString() ?? "h264" : "h264";
                        var shareBuffer = root.TryGetProperty("bufferMs", out var bufferEl) ? bufferEl.GetInt32() : 0;
                        var shareTurnOff = root.TryGetProperty("turnScreenOff", out var turnOffEl) && turnOffEl.GetBoolean();
                        var shareStayAwake = root.TryGetProperty("stayAwake", out var stayAwakeEl) && stayAwakeEl.GetBoolean();
                        var shareAudio = root.TryGetProperty("audio", out var audioEl) && audioEl.GetBoolean();
                        var shareDim = root.TryGetProperty("dimPhone", out var dimEl) && dimEl.GetBoolean();
                        var (success, message) = await _viewModel.StartScreenShare(
                            shareScreenDeviceId, shareMaxSize, shareMaxFps, shareBitRate,
                            shareCodec, shareBuffer, shareTurnOff, shareStayAwake, shareAudio, shareDim);
                        RunJs($"window.setScreenShareStatus({JsonString(shareScreenDeviceId)}, {(success ? "true" : "false")}, {JsonString(message)})");
                    }
                    break;
                case "shareApp":
                {
                    var shareAppDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var appPkg = root.GetProperty("packageName").GetString() ?? "";
                    var appLabel = root.TryGetProperty("label", out var appLabelEl) ? appLabelEl.GetString() ?? appPkg : appPkg;
                    // Only a real Android package name ever reaches scrcpy's command line.
                    if (shareAppDeviceId.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(appPkg, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$"))
                    {
                        var (appOk, appMessage) = await _viewModel.StartAppShare(shareAppDeviceId, appPkg, appLabel,
                            root.TryGetProperty("maxSize", out var aMs) ? aMs.GetInt32() : 0,
                            root.TryGetProperty("maxFps", out var aMf) ? aMf.GetInt32() : 60,
                            root.TryGetProperty("bitRate", out var aBr) ? aBr.GetInt32() : 20,
                            root.TryGetProperty("videoCodec", out var aCo) ? aCo.GetString() ?? "h264" : "h264",
                            root.TryGetProperty("bufferMs", out var aBu) ? aBu.GetInt32() : 0,
                            ReadAppShareOptions(root));
                        RunJs($"window.setAppShareStatus({JsonString(shareAppDeviceId)}, {JsonString(appPkg)}, {(appOk ? "true" : "false")}, {JsonString(appMessage)})");
                    }
                    break;
                }
                case "audioLevels":
                {
                    // The now-playing bar wants live levels (playing + visible) or not anymore.
                    var levelsOn = root.TryGetProperty("on", out var loEl) && loEl.ValueKind == JsonValueKind.True;
                    var levelsDevice = root.TryGetProperty("deviceId", out var ldEl) ? ldEl.GetString() : null;
                    if (levelsOn) _spectrum.Start(); else _spectrum.Stop();
                    if (!string.IsNullOrEmpty(levelsDevice)) _viewModel.RequestMediaLevels(levelsDevice!, levelsOn);
                    break;
                }
                case "phonePrefs":
                    if (root.TryGetProperty("statusAutoHideSeconds", out var ahEl) && ahEl.ValueKind == JsonValueKind.Number)
                        _viewModel.SetPhoneStatusAutoHide(ahEl.GetInt32());
                    if (root.TryGetProperty("accent", out var acEl) && acEl.ValueKind == JsonValueKind.String)
                        _viewModel.SetPhoneAccent(acEl.GetString() ?? "");
                    break;
                case "returnAppShare":
                    _ = Services.ScreenShareService.ReturnAppAsync(root.GetProperty("deviceId").GetString() ?? "", root.GetProperty("packageName").GetString() ?? "");
                    break;
                case "stopAllShares":
                    _viewModel.StopAllSharing(root.GetProperty("deviceId").GetString() ?? "");
                    break;
                case "stopAppShare":
                    _viewModel.StopAppShare(root.GetProperty("deviceId").GetString() ?? "", root.GetProperty("packageName").GetString() ?? "");
                    break;
                case "shareAudio":
                    var shareAudioDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    if (shareAudioDeviceId.Length > 0)
                    {
                        var (audioSuccess, audioMessage) = await _viewModel.StartAudioShare(shareAudioDeviceId);
                        RunJs($"window.setAudioShareStatus({JsonString(shareAudioDeviceId)}, {(audioSuccess ? "true" : "false")}, {JsonString(audioMessage)})");
                    }
                    break;
                case "stopAudioShare":
                    _viewModel.StopAudioShare(root.GetProperty("deviceId").GetString() ?? "");
                    break;
                case "callControl":
                    var callCtlDevice = root.GetProperty("deviceId").GetString() ?? "";
                    var callCtlAction = root.GetProperty("control").GetString() ?? "";
                    if (callCtlDevice.Length > 0 && callCtlAction.Length > 0)
                        await _viewModel.SendCallControl(callCtlDevice, callCtlAction);
                    break;
                case "stopScreenShare":
                    _viewModel.StopScreenShare(root.GetProperty("deviceId").GetString() ?? "");
                    break;
                case "sendFile":
                    var fileDeviceId = root.GetProperty("deviceId").GetString() ?? "";
                    var fileNameToSend = root.GetProperty("fileName").GetString() ?? "archivo";
                    var fileMimeType = root.GetProperty("mimeType").GetString() ?? "application/octet-stream";
                    var fileDataUrl = root.GetProperty("dataUrl").GetString() ?? "";
                    var commaIndex = fileDataUrl.IndexOf(',');
                    if (fileDeviceId.Length > 0 && commaIndex >= 0)
                    {
                        var fileBytes = Convert.FromBase64String(fileDataUrl[(commaIndex + 1)..]);
                        await _viewModel.SendFileToDevice(fileDeviceId, fileNameToSend, fileMimeType, fileBytes);
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // malformed message from the page — nothing to act on
        }
    }

    /// <summary>Opens the same folder MainViewModel.WriteDiagLog writes to in Explorer —
    /// created on demand so the button always works even before any log has arrived yet
    /// (an empty folder is itself useful diagnostic info: it means nothing has been received).</summary>
    /// <summary>Drags a received-file card's real file straight out to the OS (Explorer, an
    /// email compose window, another app, ...) — "arrastro de la notificación y la suelto en
    /// alguna carpeta" — without needing "Abrir ruta" first. The file already lives on disk (see
    /// MainViewModel.HandleIncomingFile), so this is a genuine native OS drag-and-drop
    /// (System.Windows.DragDrop.DoDragDrop with DataFormats.FileDrop), not a browser-simulated
    /// one — a plain HTML5 drag of a data: URL image doesn't produce anything a drop target
    /// outside the browser can actually use.
    ///
    /// The gesture itself is detected in JS (mousedown + movement past a small threshold on a
    /// card that HasSavedFile), which posts this message once movement is confirmed — WebView2's
    /// own mouse capture belongs to its embedded Chromium renderer, not WPF, so this can't be a
    /// normal WPF PreviewMouseMove/MouseDown handler. DoDragDrop still works when triggered this
    /// way because OLE's own drag loop tracks the CURRENT physical mouse-button state directly,
    /// not WPF's capture — it just needs the button to still be down at the moment this runs,
    /// which it is (the JS message arrives essentially instantly after the threshold is crossed,
    /// mid-gesture). Blocks until the user drops or cancels, same as dragging out of Explorer
    /// itself — nothing else needs to keep running while the user's whole attention is on the
    /// drag anyway.</summary>
    private void StartFileDrag(string key)
    {
        if (key.Length == 0) return;
        var path = _viewModel.GetSharedFilePath(key);
        if (path == null) return;
        try
        {
            var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { path });
            System.Windows.DragDrop.DoDragDrop(Browser, data, System.Windows.DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"StartFileDrag failed: {ex.Message}");
        }
    }

    private static void OpenLogsFolder()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink", "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open logs folder: {ex.Message}");
        }
    }

    /// <summary>Opens an image shown in the lightbox with Windows' native Photos app. Images live
    /// in the page only as data: URLs, so the bytes go to a temp file first. Only data:image URLs
    /// are accepted — this never writes or launches anything the page merely points at.</summary>
    private static void OpenImageInPhotos(string src)
    {
        var match = System.Text.RegularExpressions.Regex.Match(src, @"^data:image/([a-zA-Z0-9.+-]+);base64,(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return;
        var ext = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "jpg",
            "png" => "png",
            "gif" => "gif",
            "webp" => "webp",
            _ => "jpg",
        };
        try
        {
            var bytes = Convert.FromBase64String(match.Groups[2].Value);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NotiLink", "open");
            System.IO.Directory.CreateDirectory(dir);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
            var path = System.IO.Path.Combine(dir, $"imagen-{name}.{ext}");
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, bytes);
            try
            {
                Process.Start(new ProcessStartInfo($"ms-photos:viewer?fileName={path}") { UseShellExecute = true });
            }
            catch (Exception)
            {
                // Photos app missing/protocol unavailable — fall back to whatever opens images by default.
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            // Malformed data or a write failure — nothing sensible to show beyond not opening.
        }
    }

    /// <summary>Single-app window options from the page — validated here, since they end up on
    /// scrcpy's command line: only known values/shapes pass, anything else falls back to default.</summary>
    private static Services.ScreenShareService.AppShareOptions ReadAppShareOptions(JsonElement root)
    {
        static bool Flag(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var size = root.TryGetProperty("displaySize", out var ds) ? ds.GetString() ?? "" : "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(size, @"^\d{3,4}x\d{3,4}$")) size = "";
        var dpi = root.TryGetProperty("dpi", out var dp) && dp.ValueKind == JsonValueKind.Number ? dp.GetInt32() : 0;
        if (dpi is < 100 or > 800) dpi = 0;
        var ime = root.TryGetProperty("imePolicy", out var ip) ? ip.GetString() : null;
        if (ime is not ("local" or "fallback" or "hide")) ime = "local";
        return new Services.ScreenShareService.AppShareOptions(
            size, dpi, Flag(root, "decorations"), ime, Flag(root, "keepOnClose"), Flag(root, "audio"),
            Flag(root, "alwaysOnTop"), Flag(root, "borderless"), Flag(root, "restartApp"),
            !root.TryGetProperty("autoReturn", out var ar) || ar.ValueKind != JsonValueKind.False,
            !root.TryGetProperty("mirrorWhileOnPhone", out var mw) || mw.ValueKind != JsonValueKind.False);
    }

    private readonly Services.AudioSpectrumMonitor _spectrum = new();
    private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private void RunJs(string script)
    {
        if (_webViewReady && Browser.CoreWebView2 != null)
            _ = Browser.CoreWebView2.ExecuteScriptAsync(script);
        else
            _pendingScripts.Add(script);
    }

    private static readonly JsonSerializerOptions LinkPreviewJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private async Task HandleFetchLinkPreview(string url)
    {
        var preview = await LinkPreviewService.FetchAsync(url);
        RunJs($"window.setLinkPreview({JsonString(url)}, {JsonSerializer.Serialize(preview, LinkPreviewJsonOptions)})");
    }

    private static string JsonString(string? s) => JsonSerializer.Serialize(s);
}
