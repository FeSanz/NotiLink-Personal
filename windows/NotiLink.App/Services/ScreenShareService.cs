using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NotiLink.App.Services;

/// <summary>Live stats scraped from scrcpy's own console output while a mirroring session runs
/// — never invented, always what scrcpy itself reported that instant. Either field can be null
/// if scrcpy hasn't printed that particular line yet (e.g. resolution arrives once at startup,
/// fps arrives roughly once a second once --print-fps kicks in).</summary>
public readonly record struct ScreenShareStats(double? Fps, string? Resolution);

/// <summary>
/// Wraps the vendored scrcpy.exe (see windows/tools/fetch-scrcpy.ps1, CLAUDE.md's "Screen
/// sharing" section) as an external process — there's no C# scrcpy library, and reimplementing
/// its client/device protocol would be its own large project, so shelling out to the real
/// binary is the only realistic approach.
///
/// Connecting wirelessly needs the phone's ADB listening over TCP, which Android resets to
/// USB-only on every reboot unless "Depuración inalámbrica" is paired (Android 11+, done once
/// by the user in Developer options). Best-effort: if a device is currently reachable over USB,
/// this opportunistically flips it into TCP mode first so a same-session USB-then-wireless flow
/// "just works"; if that fails and no prior wireless pairing exists either, the caller gets a
/// message that explains what's needed rather than a silent failure.
/// </summary>
internal static class ScreenShareService
{
    private const int WirelessDebugPort = 5555;

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools", "scrcpy");
    private static string AdbPath => Path.Combine(ToolsDir, "adb.exe");
    private static string ScrcpyPath => Path.Combine(ToolsDir, "scrcpy.exe");

    public static bool IsAvailable => File.Exists(ScrcpyPath) && File.Exists(AdbPath);

    // Every session is keyed by the phone's deviceId: each paired phone can have its own screen
    // share and its own audio share running at the same time, independently — starting or stopping
    // one phone's never touches another's. (scrcpy targets each phone by its own IP:port, so two
    // scrcpy processes side by side don't interfere.)

    /// <summary>deviceId, stats — a new fps/resolution reading scraped from that phone's scrcpy.</summary>
    public static event Action<string, ScreenShareStats>? StatsUpdated;

    /// <summary>deviceId — that phone's video session ended, however it happened (window closed,
    /// phone dropped, StopVideo, a crash).</summary>
    public static event Action<string>? Stopped;

    /// <summary>deviceId, level — real-time peak level (0.0-1.0) of the phone whose audio is
    /// currently being measured (see AudioLevelMonitor; only one session can be measured at a time,
    /// the most recently started one carrying audio).</summary>
    public static event Action<string, float>? AudioLevelChanged;

    /// <summary>deviceId — AudioLevelMonitor never found a matching audio session for it.</summary>
    public static event Action<string>? AudioLevelUnavailable;

    /// <summary>deviceId — that phone's audio-only session ended, however it happened.</summary>
    public static event Action<string>? AudioStopped;

    static ScreenShareService()
    {
        AudioLevelMonitor.LevelChanged += level => { var d = _audioMonitorDeviceId; if (d != null) AudioLevelChanged?.Invoke(d, level); };
        AudioLevelMonitor.SessionNotFound += () => { var d = _audioMonitorDeviceId; if (d != null) AudioLevelUnavailable?.Invoke(d); };
    }

    private static readonly Regex FpsRegex = new(@"(\d+(?:\.\d+)?)\s*fps", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResolutionRegex = new(@"\b(\d{3,5}x\d{3,5})\b", RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Process> _video = new();
    private static readonly Dictionary<string, bool> _videoHasAudio = new();
    private static readonly Dictionary<string, Process> _audio = new();
    private static readonly Dictionary<string, string?> _lastResolution = new();

    public static bool IsVideoActive(string deviceId)
    {
        lock (Gate) return _video.TryGetValue(deviceId, out var p) && !p.HasExited;
    }

    public static bool IsAudioActive(string deviceId)
    {
        lock (Gate) return _audio.TryGetValue(deviceId, out var p) && !p.HasExited;
    }

    /// <summary>Attempts to start mirroring [deviceName]'s screen at [phoneIp], as [deviceId]'s
    /// own session. Every quality/behavior knob is user-configurable from the "Compartir pantalla"
    /// popup (see app.js's screen-share-overlay): maxSize (longest edge, 0 = native), maxFps
    /// (0 = uncapped), bitRateMbps, videoCodec ("h264" | "h265" | "av1"), bufferMs (extra render
    /// buffer), turnScreenOff, stayAwake (USB-only reliably), audio (forward phone audio too).
    /// Passes --print-fps and scrapes scrcpy's own output for fps/resolution (never estimated).
    /// Returns (true, status) once scrcpy is launching, or (false, explanation) — never throws.</summary>
    public static async Task<(bool Success, string Message)> StartAsync(
        string deviceId, string phoneIp, string deviceName,
        int maxSize = 0, int maxFps = 60, int bitRateMbps = 20,
        string videoCodec = "h264", int bufferMs = 0,
        bool turnScreenOff = false, bool stayAwake = false, bool audio = false, bool dimPhone = false)
    {
        if (IsVideoActive(deviceId)) return (false, $"Ya se está compartiendo la pantalla de {deviceName}.");
        if (!IsAvailable)
            return (false, "scrcpy no está instalado. Ejecuta windows\\tools\\fetch-scrcpy.ps1 y vuelve a intentar.");

        var (connected, connectMessage, target) = await EnsureConnectedAsync(phoneIp, deviceName, IsAudioActive(deviceId)).ConfigureAwait(false);
        if (!connected) return (false, connectMessage);

        var args = new List<string>
        {
            $"-s {target}",
            $"--window-title=\"NotiLink - {EscapeArg(deviceName)}\"",
            $"--video-bit-rate={Math.Clamp(bitRateMbps, 1, 100)}M",
            "--print-fps",
        };
        if (maxSize > 0) args.Add($"--max-size={maxSize}");
        if (maxFps > 0) args.Add($"--max-fps={maxFps}");
        if (videoCodec is "h265" or "av1") args.Add($"--video-codec={videoCodec}");
        if (bufferMs > 0) args.Add($"--video-buffer={bufferMs}");
        // "Atenuar" = display powered off by scrcpy while the phone itself stays awake and
        // unlocked (its screen-off timeout is stretched, see DimPhoneAsync): touching the phone
        // lights the screen up normally, like Huawei's multi-screen collaboration.
        if (turnScreenOff || dimPhone) args.Add("--turn-screen-off");
        if (stayAwake) args.Add("--stay-awake");
        if (!audio) args.Add("--no-audio");

        lock (Gate) _lastResolution[deviceId] = null;
        if (dimPhone) await DimPhoneAsync(deviceId, target).ConfigureAwait(false);

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(ScrcpyPath)
                {
                    Arguments = string.Join(' ', args),
                    WorkingDirectory = ToolsDir,
                    UseShellExecute = false,
                    // scrcpy.exe is a console-subsystem app — CreateNoWindow hides only that blank
                    // console; the SDL mirror window it opens separately is unaffected.
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.OutputDataReceived += (_, e) => HandleLine(deviceId, e.Data);
            process.ErrorDataReceived += (_, e) => HandleLine(deviceId, e.Data);
            process.Exited += (_, _) =>
            {
                lock (Gate)
                {
                    if (_video.TryGetValue(deviceId, out var cur) && ReferenceEquals(cur, process)) _video.Remove(deviceId);
                    _videoHasAudio.Remove(deviceId);
                }
                if (audio) StopAudioLevelMonitor("video", deviceId);
                if (dimPhone) _ = RestoreDimAsync(deviceId);
                Stopped?.Invoke(deviceId);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (Gate) { _video[deviceId] = process; _videoHasAudio[deviceId] = audio; }
            _ = WindowIdentity.ApplyWhenReadyAsync(process, $"NotiLink.Screen.{deviceId}",
                WindowIdentity.WithDeviceBadge(WindowIdentity.NotiLinkIcon(), "notilink", deviceId, deviceName),
                bringToFront: true);
            if (audio) StartAudioLevelMonitor(process.Id, "video", deviceId);
        }
        catch (Exception ex)
        {
            if (dimPhone) _ = RestoreDimAsync(deviceId);
            return (false, $"No se pudo iniciar scrcpy: {ex.Message}");
        }

        return (true, $"Compartiendo pantalla de {deviceName}...");
    }

    /// <summary>Stops [deviceId]'s video session, if any. Safe to call when nothing's running.</summary>
    public static void StopVideo(string deviceId)
    {
        Process? process;
        bool hadAudio;
        lock (Gate)
        {
            _video.TryGetValue(deviceId, out process);
            _video.Remove(deviceId);
            hadAudio = _videoHasAudio.TryGetValue(deviceId, out var h) && h;
            _videoHasAudio.Remove(deviceId);
        }
        if (hadAudio) StopAudioLevelMonitor("video", deviceId);
        KillQuietly(process);
    }

    /// <summary>Routes [deviceId]'s phone audio (calls excluded by Android, see CLAUDE.md) to this
    /// PC via scrcpy's audio-only mode — no quality knobs, a simple per-phone toggle.</summary>
    public static async Task<(bool Success, string Message)> StartAudioOnlyAsync(string deviceId, string phoneIp, string deviceName)
    {
        if (IsAudioActive(deviceId)) return (false, $"Ya se está transmitiendo el audio de {deviceName}.");
        if (!IsAvailable)
            return (false, "scrcpy no está instalado. Ejecuta windows\\tools\\fetch-scrcpy.ps1 y vuelve a intentar.");

        var (connected, connectMessage, target) = await EnsureConnectedAsync(phoneIp, deviceName, IsVideoActive(deviceId)).ConfigureAwait(false);
        if (!connected) return (false, connectMessage);

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(ScrcpyPath)
                {
                    Arguments = $"-s {target} --window-title=\"NotiLink - Audio de {EscapeArg(deviceName)}\" --no-video",
                    WorkingDirectory = ToolsDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };
            process.Exited += (_, _) =>
            {
                lock (Gate)
                {
                    if (_audio.TryGetValue(deviceId, out var cur) && ReferenceEquals(cur, process)) _audio.Remove(deviceId);
                }
                StopAudioLevelMonitor("audio", deviceId);
                AudioStopped?.Invoke(deviceId);
            };
            process.Start();
            lock (Gate) _audio[deviceId] = process;
            _ = HideWindowWhenReadyAsync(process);
            StartAudioLevelMonitor(process.Id, "audio", deviceId);
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo iniciar la transmisión de audio: {ex.Message}");
        }

        return (true, $"Transmitiendo el audio de {deviceName}...");
    }

    // --- Single-app sharing: the app runs on its own virtual display on the phone (scrcpy
    // --new-display + --start-app), so the PC window shows ONLY that app — no status bar, no
    // heads-up notifications, nothing else — and the phone's real screen stays free to use. Several
    // apps (and the normal full-screen share) can run side by side; keyed by deviceId + package. ---

    private static readonly Dictionary<string, Process> _apps = new();
    private static readonly Dictionary<string, string> _appLabels = new();
    private static readonly Dictionary<string, AppSession> _appSessions = new();

    /// <summary>Where a shared app is right now — the phone can take it back: opening the same
    /// app on the phone moves its task off the virtual display (the PC window goes black), and
    /// swiping it away from Recents ends it. A monitor per session watches for both.</summary>
    private sealed class AppSession
    {
        public required string Target { get; init; }
        public required bool AutoReturn { get; init; }
        public required string DeviceId { get; init; }
        public required string PackageName { get; init; }
        public required string Label { get; init; }
        public required string DeviceName { get; init; }
        public volatile bool StoppingCompanion;   // our own swap-back, not the user closing it
        public required AppShareOptions Options { get; init; }
        public required byte[]? IconPng { get; init; }
        public required int MaxSize { get; init; }
        public required int MaxFps { get; init; }
        public required int BitRateMbps { get; init; }
        public required string VideoCodec { get; init; }
        public Process? Window;              // the app's own scrcpy window
        public Process? Companion;           // phone-screen mirror shown while the app is on the phone
        public int? DisplayId;
        public string State = "pc";          // "pc" | "phone"
        public string? LauncherActivity;
    }

    /// <summary>The phone's identifier changed: re-badge the taskbar icon of everything already
    /// open from it (full screen + each app window / its companion mirror).</summary>
    public static void RefreshDeviceIcons(string deviceId, string deviceName)
    {
        Process? video;
        List<AppSession> sessions;
        lock (Gate)
        {
            _video.TryGetValue(deviceId, out video);
            sessions = _appSessions.Values.Where(s => s.DeviceId == deviceId).ToList();
        }
        if (video is { HasExited: false })
            _ = WindowIdentity.ApplyWhenReadyAsync(video, $"NotiLink.Screen.{deviceId}",
                WindowIdentity.WithDeviceBadge(WindowIdentity.NotiLinkIcon(), "notilink", deviceId, deviceName));
        foreach (var s in sessions)
        {
            var icon = AppIcon(s);
            foreach (var p in new[] { s.Window, s.Companion })
                if (p is { HasExited: false }) _ = WindowIdentity.ApplyWhenReadyAsync(p, AppWindowId(s.DeviceId, s.PackageName), icon);
        }
    }

    private static string AppWindowId(string deviceId, string packageName) => $"NotiLink.App.{deviceId}.{packageName}";

    // The app's own icon (NotiLink's if unknown) + a badge with the phone's initial/color.
    private static System.Drawing.Icon? AppIcon(AppSession s) =>
        WindowIdentity.WithDeviceBadge(
            WindowIdentity.FromPng("app:" + s.PackageName, s.IconPng) ?? WindowIdentity.NotiLinkIcon(),
            "app:" + s.PackageName, s.DeviceId, s.DeviceName);

    /// <summary>deviceId, packageName, state ("pc" = showing on the PC, "phone" = the user
    /// opened it on the phone's own screen, so the PC window is empty for now).</summary>
    public static event Action<string, string, string>? AppStateChanged;

    public static string AppState(string deviceId, string packageName)
    {
        lock (Gate) return _appSessions.TryGetValue(AppKey(deviceId, packageName), out var s) ? s.State : "pc";
    }
    private static string AppKey(string deviceId, string packageName) => deviceId + "|" + packageName;

    /// <summary>deviceId, packageName — that app's window/session ended, however it happened.</summary>
    public static event Action<string, string>? AppStopped;

    public static bool IsAppActive(string deviceId, string packageName)
    {
        lock (Gate) return _apps.TryGetValue(AppKey(deviceId, packageName), out var p) && !p.HasExited;
    }

    public static IReadOnlyList<string> ActiveApps(string deviceId)
    {
        lock (Gate)
            return _apps.Where(kv => kv.Key.StartsWith(deviceId + "|", StringComparison.Ordinal) && !kv.Value.HasExited)
                .Select(kv => kv.Key[(deviceId.Length + 1)..]).ToList();
    }

    /// <summary>How a single-app window behaves — every field maps to one scrcpy option and is
    /// set from the "Solo una app" tab (remembered there).
    ///   DisplaySize   — virtual display "WxH" ("" = same as the phone's screen).
    ///   Dpi           — virtual display density (0 = same as the phone): lower = more content.
    ///   Decorations   — show the launcher, status bar (clock/battery) and navigation bar.
    ///   ImePolicy     — keyboard: "local" (in the window), "fallback" (on the phone), "hide".
    ///   KeepOnClose   — closing the window moves the app to the phone's screen instead of closing it.
    ///   Audio         — also forward the phone's audio (the whole phone's, not just this app's).
    ///   AlwaysOnTop / Borderless — window placement/look on the PC.
    ///   RestartApp    — force-stop the app first, so it always opens fresh.
    ///   AutoReturn    — after the app was opened on the phone and the user leaves it there, move
    ///                   it back to the PC window automatically.
    ///   MirrorWhileOnPhone — while the app is on the phone's own screen, show that screen in a
    ///                   window right over the (now empty) app window, until it comes back.</summary>
    public sealed record AppShareOptions(
        string DisplaySize = "", int Dpi = 0, bool Decorations = false, string ImePolicy = "local",
        bool KeepOnClose = false, bool Audio = false, bool AlwaysOnTop = false, bool Borderless = false,
        bool RestartApp = false, bool AutoReturn = true, bool MirrorWhileOnPhone = true);

    /// <summary>(packageName, label) of every app [deviceId] is currently sharing.</summary>
    public static IReadOnlyList<(string Package, string Label, string State)> ActiveAppsWithLabels(string deviceId)
    {
        lock (Gate)
            return _apps.Where(kv => kv.Key.StartsWith(deviceId + "|", StringComparison.Ordinal) && !kv.Value.HasExited)
                .Select(kv => (kv.Key[(deviceId.Length + 1)..],
                               _appLabels.GetValueOrDefault(kv.Key, kv.Key[(deviceId.Length + 1)..]),
                               _appSessions.TryGetValue(kv.Key, out var s) ? s.State : "pc"))
                .ToList();
    }

    public static async Task<(bool Success, string Message)> StartAppAsync(
        string deviceId, string phoneIp, string deviceName, string packageName, string appLabel,
        int maxSize, int maxFps, int bitRateMbps, string videoCodec, int bufferMs, AppShareOptions options, byte[]? iconPng = null)
    {
        if (IsAppActive(deviceId, packageName)) return (false, $"{appLabel} ya se está compartiendo.");
        if (!IsAvailable)
            return (false, "scrcpy no está instalado. Ejecuta windows\\tools\\fetch-scrcpy.ps1 y vuelve a intentar.");

        var (connected, connectMessage, target) = await EnsureConnectedAsync(
            phoneIp, deviceName, IsVideoActive(deviceId) || IsAudioActive(deviceId) || ActiveApps(deviceId).Count > 0).ConfigureAwait(false);
        if (!connected) return (false, connectMessage);

        var args = new List<string>
        {
            $"-s {target}",
            $"--window-title=\"{EscapeArg(appLabel)} - NotiLink ({EscapeArg(deviceName)})\"",
            options.DisplaySize.Length > 0 || options.Dpi > 0
                ? $"--new-display={options.DisplaySize}{(options.Dpi > 0 ? "/" + options.Dpi : "")}"
                : "--new-display",
            $"--start-app={(options.RestartApp ? "+" : "")}{packageName}",
            $"--display-ime-policy={options.ImePolicy}",
            $"--video-bit-rate={Math.Clamp(bitRateMbps, 1, 100)}M",
        };
        // Without decorations the virtual display shows ONLY the app (no launcher, clock, battery
        // or navigation bar); closing the app leaves the window black instead of the home screen.
        if (!options.Decorations) args.Add("--no-vd-system-decorations");
        if (options.KeepOnClose) args.Add("--no-vd-destroy-content");
        if (!options.Audio) args.Add("--no-audio");
        if (options.AlwaysOnTop) args.Add("--always-on-top");
        if (options.Borderless) args.Add("--window-borderless");
        if (maxSize > 0) args.Add($"--max-size={maxSize}");
        if (maxFps > 0) args.Add($"--max-fps={maxFps}");
        if (videoCodec is "h265" or "av1") args.Add($"--video-codec={videoCodec}");
        if (bufferMs > 0) args.Add($"--video-buffer={bufferMs}");

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(ScrcpyPath)
                {
                    Arguments = string.Join(' ', args),
                    WorkingDirectory = ToolsDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            var key = AppKey(deviceId, packageName);
            var session = new AppSession
            {
                Target = target, AutoReturn = options.AutoReturn, DeviceId = deviceId, PackageName = packageName,
                Label = appLabel, DeviceName = deviceName, Options = options, IconPng = iconPng, MaxSize = maxSize, MaxFps = maxFps,
                BitRateMbps = bitRateMbps, VideoCodec = videoCodec, Window = process,
            };
            // scrcpy prints "New display: 1080x2340/450 (id=48)" once the virtual display exists.
            void OnLine(string? line)
            {
                if (line == null) return;
                var m = NewDisplayRegex.Match(line);
                if (m.Success) lock (Gate) session.DisplayId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.Exited += (_, _) =>
            {
                lock (Gate)
                {
                    if (_apps.TryGetValue(key, out var cur) && ReferenceEquals(cur, process))
                    {
                        _apps.Remove(key);
                        _appLabels.Remove(key);
                        _appSessions.Remove(key);
                    }
                }
                StopCompanion(session);
                AppStopped?.Invoke(deviceId, packageName);
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (Gate) { _apps[key] = process; _appLabels[key] = appLabel; _appSessions[key] = session; }
            _ = WindowIdentity.ApplyWhenReadyAsync(process, AppWindowId(deviceId, packageName), AppIcon(session),
                bringToFront: true, keepTopmost: options.AlwaysOnTop);
            _ = MonitorAppAsync(deviceId, packageName, process, session);
        }
        catch (Exception ex)
        {
            return (false, $"No se pudo iniciar scrcpy: {ex.Message}");
        }
        return (true, $"Abriendo {appLabel} de {deviceName}...");
    }

    private static readonly Regex NewDisplayRegex = new(@"New display: \S+ \(id=(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex DisplayHeaderRegex = new(@"^Display #(\d+)", RegexOptions.Compiled);

    /// <summary>Every ~2.5 s: where is this app's task? On the virtual display → showing on the PC.
    /// On another display → the user opened it on the phone (PC window black); once they leave it
    /// there (task no longer visible) it's moved back when AutoReturn is on. Nowhere at all (twice
    /// in a row, after it was seen) → closed from the phone's Recents, so the share is stopped.</summary>
    private static async Task MonitorAppAsync(string deviceId, string packageName, Process process, AppSession session)
    {
        var seen = false;
        var misses = 0;
        // Top-level task lines only ("  * Task{" — 2-space indent); deeper ones repeat the same
        // tasks under other groupings without their display header.
        var taskRegex = new Regex(@"^  \* Task\{.*(?:A=\d+:" + Regex.Escape(packageName) + @"(?:\s|$)|I=" + Regex.Escape(packageName) + "/)");
        while (!process.HasExited)
        {
            await Task.Delay(2500).ConfigureAwait(false);
            int? ourDisplay;
            lock (Gate) ourDisplay = session.DisplayId;
            if (ourDisplay == null || process.HasExited) continue;

            var dump = await RunAdbAsync($"-s {session.Target} shell \"dumpsys activity activities | grep -E '^Display #|^  \\* Task\\{{'\"").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(dump)) continue; // adb hiccup — no conclusion this round

            int? currentDisplay = null, foundOn = null;
            var visible = false;
            foreach (var raw in dump.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var h = DisplayHeaderRegex.Match(line);
                if (h.Success) { currentDisplay = int.Parse(h.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
                if (currentDisplay != null && taskRegex.IsMatch(line))
                {
                    foundOn = currentDisplay;
                    visible = line.Contains("visible=true", StringComparison.Ordinal);
                    if (foundOn == ourDisplay) break;
                }
            }

            if (foundOn == null)
            {
                if (seen && ++misses >= 2)
                {
                    PcDiagLog.Write($"app share: {packageName} closed on the phone, stopping");
                    StopApp(deviceId, packageName);
                    return;
                }
                continue;
            }
            misses = 0;
            seen = true;
            var state = foundOn == ourDisplay ? "pc" : "phone";
            bool changed;
            lock (Gate) { changed = session.State != state; session.State = state; }
            if (changed)
            {
                AppStateChanged?.Invoke(deviceId, packageName, state);
                if (state == "phone") StartCompanion(session);
                else StopCompanion(session);
            }
            if (state == "phone" && !visible && session.AutoReturn)
                await ReturnAppAsync(deviceId, packageName).ConfigureAwait(false);
        }
    }

    /// <summary>Moves the app back onto its virtual display (the PC window) — "Traer a la PC".</summary>
    public static async Task ReturnAppAsync(string deviceId, string packageName)
    {
        AppSession? session;
        lock (Gate) _appSessions.TryGetValue(AppKey(deviceId, packageName), out session);
        if (session?.DisplayId is not { } displayId) return;
        if (session.LauncherActivity == null)
        {
            var resolved = await RunAdbAsync($"-s {session.Target} shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER {packageName}").ConfigureAwait(false);
            var last = resolved.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('/'));
            if (last == null) return;
            session.LauncherActivity = last;
        }
        await RunAdbAsync($"-s {session.Target} shell am start --display {displayId} -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n {session.LauncherActivity}").ConfigureAwait(false);
        bool changed;
        lock (Gate) { changed = session.State != "pc"; session.State = "pc"; }
        StopCompanion(session);
        // Back on the PC: show it in front, not wherever it was in the z-order.
        var returnedHwnd = AppWindowHandle(session);
        if (returnedHwnd != IntPtr.Zero) WindowIdentity.BringToFront(returnedHwnd, session.Options.AlwaysOnTop);
        if (changed) AppStateChanged?.Invoke(deviceId, packageName, "pc");
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    private const int SwShow = 5;
    private const uint SwpNoZOrder = 0x0004;

    /// <summary>While the shared app is on the phone's own screen, its PC window is swapped for a
    /// mirror of the phone's screen at the same spot — ONE window either way (the app window is
    /// hidden, not stacked under it, so there's never a confusing "copy"). Swapped back when the
    /// app returns. Closing the mirror window is closing the share. Skipped if this phone's full
    /// screen is already being shared.</summary>
    private static void StartCompanion(AppSession session)
    {
        if (!session.Options.MirrorWhileOnPhone || IsVideoActive(session.DeviceId)) return;
        lock (Gate) { if (session.Companion is { HasExited: false }) return; }
        var args = new List<string>
        {
            $"-s {session.Target}",
            $"--window-title=\"{EscapeArg(session.Label)} · en el celular - NotiLink ({EscapeArg(session.DeviceName)})\"",
            "--no-audio",
            $"--video-bit-rate={Math.Clamp(session.BitRateMbps, 1, 100)}M",
        };
        if (session.MaxSize > 0) args.Add($"--max-size={session.MaxSize}");
        if (session.MaxFps > 0) args.Add($"--max-fps={session.MaxFps}");
        if (session.VideoCodec is "h265" or "av1") args.Add($"--video-codec={session.VideoCodec}");
        if (session.Options.AlwaysOnTop) args.Add("--always-on-top");
        if (session.Options.Borderless) args.Add("--window-borderless");
        try
        {
            var appHwnd = AppWindowHandle(session);
            if (appHwnd != IntPtr.Zero && GetWindowRect(appHwnd, out var r) && r.Right > r.Left)
            {
                args.Add($"--window-x={r.Left}");
                args.Add($"--window-y={r.Top}");
                args.Add($"--window-width={r.Right - r.Left}");
                args.Add($"--window-height={r.Bottom - r.Top}");
            }
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(ScrcpyPath)
                {
                    Arguments = string.Join(' ', args),
                    WorkingDirectory = ToolsDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };
            p.Exited += (_, _) =>
            {
                // The user closed the mirror window → they're done with this app on the PC.
                if (!session.StoppingCompanion && session.Window is { HasExited: false })
                    StopApp(session.DeviceId, session.PackageName);
            };
            session.StoppingCompanion = false;
            p.Start();
            lock (Gate) session.Companion = p;
            // Same taskbar identity/icon as the app window it stands in for.
            _ = WindowIdentity.ApplyWhenReadyAsync(p, AppWindowId(session.DeviceId, session.PackageName), AppIcon(session));
            _ = HideAppWindowWhenMirrorShowsAsync(session, p, appHwnd);
        }
        catch (Exception ex)
        {
            PcDiagLog.Write($"app share: companion mirror failed: {ex.Message}");
        }
    }

    private static IntPtr AppWindowHandle(AppSession session)
    {
        var win = session.Window;
        if (win is not { HasExited: false }) return IntPtr.Zero;
        win.Refresh();
        return win.MainWindowHandle;
    }

    // Hide the app window only once the mirror window actually exists (no blank gap in between).
    private static async Task HideAppWindowWhenMirrorShowsAsync(AppSession session, Process mirror, IntPtr appHwnd)
    {
        if (appHwnd == IntPtr.Zero) return;
        for (var i = 0; i < 40 && !mirror.HasExited; i++)
        {
            mirror.Refresh();
            if (mirror.MainWindowHandle != IntPtr.Zero)
            {
                if (ReferenceEquals(session.Companion, mirror)) ShowWindow(appHwnd, SwHide);
                return;
            }
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    /// <summary>Swap back: the app window reappears where the mirror window was (if the user moved
    /// or resized it meanwhile, the app window follows), then the mirror closes.</summary>
    private static void StopCompanion(AppSession session)
    {
        Process? p;
        lock (Gate) { p = session.Companion; session.Companion = null; }
        if (p == null) return;
        session.StoppingCompanion = true;
        try
        {
            var appHwnd = AppWindowHandle(session);
            if (appHwnd != IntPtr.Zero)
            {
                if (!p.HasExited)
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero && GetWindowRect(p.MainWindowHandle, out var r) && r.Right > r.Left)
                        SetWindowPos(appHwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpNoZOrder);
                }
                ShowWindow(appHwnd, SwShow);
            }
        }
        catch (Exception) { }
        KillQuietly(p);
    }

    public static void StopApp(string deviceId, string packageName)
    {
        Process? process;
        lock (Gate)
        {
            var key = AppKey(deviceId, packageName);
            _apps.TryGetValue(key, out process);
            _apps.Remove(key);
        }
        KillQuietly(process);
    }

    public static void StopAllApps(string deviceId)
    {
        foreach (var pkg in ActiveApps(deviceId)) StopApp(deviceId, pkg);
    }

    /// <summary>Stops [deviceId]'s audio-only session, if any.</summary>
    public static void StopAudio(string deviceId)
    {
        Process? process;
        lock (Gate)
        {
            _audio.TryGetValue(deviceId, out process);
            _audio.Remove(deviceId);
        }
        StopAudioLevelMonitor("audio", deviceId);
        KillQuietly(process);
    }

    // --- "Atenuar el celular": like Huawei's multi-screen collaboration, the phone's display goes
    // dark (scrcpy --turn-screen-off) but the phone stays awake and unlocked — touching it lights
    // the screen up again. What would otherwise put it to sleep and lock it is the screen-off
    // timeout, so it's stretched over adb (the shell user may write system settings, no extra
    // permission needed) and the original is kept in dim-restore.json until put back: a crash or
    // forced close of the PC app can't leave the phone never sleeping, the next share restores it. ---

    private sealed record DimBackup(string Target, string Timeout);

    private static readonly object DimGate = new();
    private static string DimFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink", "dim-restore.json");

    private static Dictionary<string, DimBackup> LoadDim()
    {
        try
        {
            return File.Exists(DimFile)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DimBackup>>(File.ReadAllText(DimFile)) ?? new()
                : new();
        }
        catch (Exception) { return new(); }
    }

    private static void SaveDim(Dictionary<string, DimBackup> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DimFile)!);
            if (map.Count == 0) File.Delete(DimFile);
            else File.WriteAllText(DimFile, System.Text.Json.JsonSerializer.Serialize(map));
        }
        catch (Exception) { }
    }

    private static async Task DimPhoneAsync(string deviceId, string target)
    {
        try
        {
            // A leftover backup means a previous session never restored: put it back first, so
            // what's saved below is the real original and not our own stretched value.
            await RestoreDimAsync(deviceId).ConfigureAwait(false);

            var timeout = (await RunAdbAsync($"-s {target} shell settings get system screen_off_timeout").ConfigureAwait(false)).Trim();
            if (!long.TryParse(timeout, out _)) return;

            lock (DimGate)
            {
                var map = LoadDim();
                map[deviceId] = new DimBackup(target, timeout);
                SaveDim(map);
            }
            await RunAdbAsync($"-s {target} shell settings put system screen_off_timeout 86400000").ConfigureAwait(false);
            PcDiagLog.Write($"dim: {deviceId} sin bloqueo automático (tiempo de apagado {timeout} ms guardado)");
        }
        catch (Exception) { }
    }

    /// <summary>Puts back [deviceId]'s original screen-off timeout if a dim is pending. If the
    /// phone can't be reached right now the backup is kept and retried at the next share.</summary>
    public static async Task RestoreDimAsync(string deviceId)
    {
        DimBackup? backup;
        lock (DimGate) LoadDim().TryGetValue(deviceId, out backup);
        if (backup == null) return;
        try
        {
            var o = await RunAdbAsync($"-s {backup.Target} shell settings put system screen_off_timeout {backup.Timeout}").ConfigureAwait(false);
            if (o.Contains("error", StringComparison.OrdinalIgnoreCase) || o.Contains("offline", StringComparison.OrdinalIgnoreCase)) return;
            lock (DimGate)
            {
                var map = LoadDim();
                map.Remove(deviceId);
                SaveDim(map);
            }
            PcDiagLog.Write($"dim: {deviceId} restaurado");
        }
        catch (Exception) { }
    }

    private static void KillQuietly(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone — nothing to stop.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SwHide = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    /// <summary>scrcpy's own window (its default icon, not NotiLink's) gets its process a real
    /// HWND a moment after Process.Start(), not instantly — this polls for it, up to ~6s, for
    /// both StartAsync (sets NotiLink's own icon on it) and StartAudioOnlyAsync (hides it
    /// outright) to build on.</summary>
    private static async Task<IntPtr> WaitForWindowAsync(Process process)
    {
        for (var i = 0; i < 30 && !process.HasExited; i++)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            await Task.Delay(200).ConfigureAwait(false);
        }
        return IntPtr.Zero;
    }

    /// <summary>scrcpy still opens a small window even with --no-video. Audio keeps flowing
    /// either way — the window is just a status/close-affordance shell around the underlying ADB
    /// audio pipe, not something the pipe depends on being visible. The header's own
    /// audio-share button (already a solid-purple icon while active, see audio-share-btn.active
    /// in app.css) is what represents this session's state instead — stopping it goes through
    /// that button/StopAudio(), not a window to close.</summary>
    private static async Task HideWindowWhenReadyAsync(Process process)
    {
        var hwnd = await WaitForWindowAsync(process).ConfigureAwait(false);
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SwHide);
    }

    // AudioLevelMonitor can watch one process at a time — it follows whichever audio-carrying
    // session started most recently. Ownership is "kind:deviceId", so stopping one session never
    // tears down a watch another (possibly another phone's) actually owns.
    private static string? _audioMonitorOwner;
    private static string? _audioMonitorDeviceId;

    private static void StartAudioLevelMonitor(int processId, string kind, string deviceId)
    {
        AudioLevelMonitor.Start(processId);
        _audioMonitorOwner = $"{kind}:{deviceId}";
        _audioMonitorDeviceId = deviceId;
    }

    private static void StopAudioLevelMonitor(string kind, string deviceId)
    {
        if (_audioMonitorOwner != $"{kind}:{deviceId}") return;
        AudioLevelMonitor.Stop();
        _audioMonitorOwner = null;
        _audioMonitorDeviceId = null;
    }

    /// <summary>Picks the adb target for THIS phone without disturbing any other one. Two phones
    /// at once used to collide: every share start ran "adb -d tcpip", which restarts the adb
    /// daemon of whatever phone is on the USB cable — even when the share was for the OTHER phone
    /// over Wi-Fi — killing the mirror that cable phone already had running. Now:
    ///   1. this phone's IP:5555 already connected → use it as is;
    ///   2. this phone is on USB (matched by its Wi-Fi IP) → switch ONLY this phone to Wi-Fi
    ///      debugging ("adb -s serial tcpip"), so later shares work without the cable — unless it
    ///      already has a session running (that restart would kill it); then just use the USB
    ///      connection as is;
    ///   3. otherwise "adb connect" (works when Wireless debugging was set up earlier).</summary>
    private static async Task<(bool Success, string Message, string Target)> EnsureConnectedAsync(
        string phoneIp, string deviceName, bool phoneHasActiveSession)
    {
        var target = $"{phoneIp}:{WirelessDebugPort}";
        var devices = ParseAdbDevices(await RunAdbAsync("devices").ConfigureAwait(false));
        if (devices.Contains(target)) return (true, "", target);

        foreach (var serial in devices.Where(d => !d.Contains(':')))
        {
            var addr = await RunAdbAsync($"-s {serial} shell ip -f inet addr show wlan0").ConfigureAwait(false);
            if (!addr.Contains($"inet {phoneIp}/", StringComparison.Ordinal)) continue;
            if (phoneHasActiveSession) return (true, "", serial);
            await RunAdbAsync($"-s {serial} tcpip {WirelessDebugPort}").ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            var viaWifi = await RunAdbAsync($"connect {target}").ConfigureAwait(false);
            return viaWifi.Contains("connected", StringComparison.OrdinalIgnoreCase) && !viaWifi.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                ? (true, "", target)
                : (true, "", serial);
        }

        var connectOutput = await RunAdbAsync($"connect {target}").ConfigureAwait(false);
        if (connectOutput.Contains("connected", StringComparison.OrdinalIgnoreCase) &&
            !connectOutput.Contains("cannot", StringComparison.OrdinalIgnoreCase))
            return (true, "", target);

        return (false,
            $"No se pudo conectar a {deviceName}. Conéctalo por USB (con \"Depuración USB\" activa) " +
            "o activa \"Depuración inalámbrica\" en Opciones de desarrollador del celular y vuelve a intentar.", target);
    }

    /// <summary>Serials adb reports as ready ("device" state) — USB serials and ip:port alike.</summary>
    private static HashSet<string> ParseAdbDevices(string output) =>
        output.Split('\n')
            .Select(l => l.Trim().Split('\t', ' '))
            .Where(p => p.Length >= 2 && p[^1] == "device" && !p[0].StartsWith("List", StringComparison.Ordinal))
            .Select(p => p[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void HandleLine(string deviceId, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        double? fps = null;
        var fpsMatch = FpsRegex.Match(line);
        if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            fps = f;

        string? resolution;
        var resMatch = ResolutionRegex.Match(line);
        lock (Gate)
        {
            if (resMatch.Success) _lastResolution[deviceId] = resMatch.Value;
            _lastResolution.TryGetValue(deviceId, out resolution);
        }

        if (fps != null || resMatch.Success)
            StatsUpdated?.Invoke(deviceId, new ScreenShareStats(fps, resolution));
    }

    private static async Task<string> RunAdbAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(AdbPath, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ToolsDir,
            };
            using var process = Process.Start(psi);
            if (process == null) return "";
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return stdout + stderr;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string EscapeArg(string s) => s.Replace("\"", "'");
}
