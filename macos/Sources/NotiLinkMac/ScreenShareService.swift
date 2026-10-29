import Foundation
import NotiLinkCore

/// Wraps the vendored `scrcpy`/`adb` binaries (macos/tools/fetch-scrcpy.sh) as external
/// processes — the macOS equivalent of ScreenShareService.cs. Video and audio-only sharing are
/// tracked as fully independent per-deviceId processes, exactly like the Windows build: starting
/// or stopping one never touches the other for the same phone.
public struct ScreenShareSettings {
    public var maxSize: Int
    public var maxFps: Int
    public var videoBitRateMbps: Int
    public var videoCodec: String // "h264" (default, omitted) | "h265" | "av1"
    public var videoBufferMs: Int
    public var turnScreenOff: Bool
    public var stayAwake: Bool
    public var audio: Bool
    public var dimPhone: Bool

    public init(maxSize: Int, maxFps: Int, videoBitRateMbps: Int, videoCodec: String,
                videoBufferMs: Int, turnScreenOff: Bool, stayAwake: Bool, audio: Bool, dimPhone: Bool) {
        self.maxSize = maxSize
        self.maxFps = maxFps
        self.videoBitRateMbps = videoBitRateMbps
        self.videoCodec = videoCodec
        self.videoBufferMs = videoBufferMs
        self.turnScreenOff = turnScreenOff
        self.stayAwake = stayAwake
        self.audio = audio
        self.dimPhone = dimPhone
    }
}

public struct ScreenShareStats: Equatable {
    public let fps: Double?
    public let resolution: String?
}

public enum ScreenShareKind {
    case video, audio
}

/// Every field maps to one scrcpy option, set from the "Solo una app" tab in wwwroot/app.js and
/// sent as-is on every `shareApp` action — mirrors `AppShareOptions` (C#). Deliberately missing
/// `MirrorWhileOnPhone`: that one needs Windows' Win32 cross-process window manipulation, which
/// has no macOS equivalent without the Accessibility permission — see ScreenShareService.swift's
/// header note and CLAUDE.md's "macOS PC-side build" section for why it's not built here.
public struct AppShareOptions {
    public var displaySize: String
    public var dpi: Int
    public var decorations: Bool
    public var imePolicy: String // "local" | "fallback" | "hide"
    public var keepOnClose: Bool
    public var audio: Bool
    public var alwaysOnTop: Bool
    public var borderless: Bool
    public var restartApp: Bool
    public var autoReturn: Bool

    public init(displaySize: String, dpi: Int, decorations: Bool, imePolicy: String, keepOnClose: Bool,
                audio: Bool, alwaysOnTop: Bool, borderless: Bool, restartApp: Bool, autoReturn: Bool) {
        self.displaySize = displaySize
        self.dpi = dpi
        self.decorations = decorations
        self.imePolicy = imePolicy
        self.keepOnClose = keepOnClose
        self.audio = audio
        self.alwaysOnTop = alwaysOnTop
        self.borderless = borderless
        self.restartApp = restartApp
        self.autoReturn = autoReturn
    }
}

/// Where a shared app is right now — mirrors `AppSession` (C#), minus everything related to the
/// companion-mirror window (not built here, see `AppShareOptions`'s header note).
final class AppShareSession {
    let target: String
    let deviceId: String
    let packageName: String
    let label: String
    let process: Process
    let autoReturn: Bool
    var displayId: Int?
    var state: String = "pc" // "pc" | "phone"
    var launcherActivity: String?
    var monitorTask: Task<Void, Never>?

    init(target: String, deviceId: String, packageName: String, label: String, process: Process, autoReturn: Bool) {
        self.target = target
        self.deviceId = deviceId
        self.packageName = packageName
        self.label = label
        self.process = process
        self.autoReturn = autoReturn
    }
}

public final class ScreenShareService {
    /// Fired whenever a fresh fps/resolution line is scraped from the video process's own
    /// stdout/stderr — mirrors `HandleLine`'s `FpsRegex`/`ResolutionRegex`, never invented.
    public var onStatsUpdated: ((String, ScreenShareStats) -> Void)?
    /// Fired when a session ends, whether by explicit stop or the process dying on its own.
    public var onStopped: ((String, ScreenShareKind) -> Void)?
    /// (deviceId, packageName, state) — fired when a shared app's task moves between the virtual
    /// display (PC) and the phone's own screen, or back.
    public var onAppStateChanged: ((String, String, String) -> Void)?
    /// (deviceId, packageName) — that app's share ended, however it happened.
    public var onAppStopped: ((String, String) -> Void)?

    private let diagLog: PcDiagLog?
    private let toolsDir: URL
    private let scrcpyPath: URL
    private let adbPath: URL
    private let queue = DispatchQueue(label: "notilink.screenshare")

    private var videoProcesses: [String: Process] = [:]
    private var audioProcesses: [String: Process] = [:]
    private var lastResolution: [String: String] = [:]
    private var appSessions: [String: AppShareSession] = [:] // "deviceId|packageName" -> session

    private static let wirelessPort = 5555
    private static let fpsRegex = try! NSRegularExpression(pattern: #"(\d+(?:\.\d+)?)\s*fps"#, options: [.caseInsensitive])
    private static let resolutionRegex = try! NSRegularExpression(pattern: #"\b(\d{3,5}x\d{3,5})\b"#)
    private static let newDisplayRegex = try! NSRegularExpression(pattern: #"New display: \S+ \(id=(\d+)\)"#)
    private static let displayHeaderRegex = try! NSRegularExpression(pattern: #"^Display #(\d+)"#)

    public init(diagLog: PcDiagLog? = nil) {
        self.diagLog = diagLog
        let resourceDir = Bundle.main.resourceURL ?? Bundle.main.bundleURL
        self.toolsDir = resourceDir.appendingPathComponent("tools/scrcpy", isDirectory: true)
        self.scrcpyPath = toolsDir.appendingPathComponent("scrcpy")
        self.adbPath = toolsDir.appendingPathComponent("adb")
    }

    public var isAvailable: Bool {
        FileManager.default.isExecutableFile(atPath: scrcpyPath.path) && FileManager.default.isExecutableFile(atPath: adbPath.path)
    }

    private static let notInstalledMessage = "scrcpy no está instalado. Ejecuta macos/tools/fetch-scrcpy.sh y vuelve a intentar."
    private static func connectFailedMessage(_ deviceName: String) -> String {
        "No se pudo conectar a \(deviceName). Conéctalo por USB (con \"Depuración USB\" activa) o activa \"Depuración inalámbrica\" en Opciones de desarrollador del celular y vuelve a intentar."
    }

    public func isVideoActive(deviceId: String) -> Bool {
        queue.sync { videoProcesses[deviceId]?.isRunning ?? false }
    }

    public func isAudioActive(deviceId: String) -> Bool {
        queue.sync { audioProcesses[deviceId]?.isRunning ?? false }
    }

    public func isAppActive(deviceId: String, packageName: String) -> Bool {
        queue.sync { appSessions[Self.appKey(deviceId, packageName)] != nil }
    }

    public func activeApps(deviceId: String) -> [(packageName: String, label: String, state: String)] {
        queue.sync {
            appSessions.values
                .filter { $0.deviceId == deviceId }
                .map { ($0.packageName, $0.label, $0.state) }
        }
    }

    private static func appKey(_ deviceId: String, _ packageName: String) -> String { "\(deviceId)|\(packageName)" }

    // MARK: - Start

    public func startVideo(deviceId: String, ip: String, deviceName: String, settings: ScreenShareSettings) async -> (success: Bool, message: String) {
        guard isAvailable else { return (false, Self.notInstalledMessage) }

        let alreadyActive = isVideoActive(deviceId: deviceId) || isAudioActive(deviceId: deviceId)
        guard let target = await ensureConnected(ip: ip, phoneHasActiveSession: alreadyActive) else {
            return (false, Self.connectFailedMessage(deviceName))
        }

        var args = [
            "-s", target,
            "--window-title=NotiLink - \(deviceName)",
            "--video-bit-rate=\(min(max(settings.videoBitRateMbps, 1), 100))M",
            "--print-fps",
        ]
        if settings.maxSize > 0 { args.append("--max-size=\(settings.maxSize)") }
        if settings.maxFps > 0 { args.append("--max-fps=\(settings.maxFps)") }
        if settings.videoCodec == "h265" || settings.videoCodec == "av1" { args.append("--video-codec=\(settings.videoCodec)") }
        if settings.videoBufferMs > 0 { args.append("--video-buffer=\(settings.videoBufferMs)") }
        if settings.turnScreenOff || settings.dimPhone { args.append("--turn-screen-off") }
        if settings.stayAwake { args.append("--stay-awake") }
        if !settings.audio { args.append("--no-audio") }

        let process = makeScrcpyProcess(args: args)
        let outPipe = Pipe()
        let errPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        process.terminationHandler = { [weak self] _ in self?.handleTermination(deviceId: deviceId, kind: .video) }

        do {
            try process.run()
        } catch {
            diagLog?.log("scrcpy video launch failed for \(deviceId): \(error)")
            return (false, "No se pudo iniciar scrcpy: \(error.localizedDescription)")
        }

        queue.sync { videoProcesses[deviceId] = process }
        attachOutputScraper(outPipe, deviceId: deviceId)
        attachOutputScraper(errPipe, deviceId: deviceId)
        diagLog?.log("Started video share for \(deviceId) -> \(target)")
        return (true, "Compartiendo pantalla de \(deviceName)")
    }

    public func startAudioOnly(deviceId: String, ip: String, deviceName: String) async -> (success: Bool, message: String) {
        guard isAvailable else { return (false, Self.notInstalledMessage) }

        let alreadyActive = isVideoActive(deviceId: deviceId) || isAudioActive(deviceId: deviceId)
        guard let target = await ensureConnected(ip: ip, phoneHasActiveSession: alreadyActive) else {
            return (false, Self.connectFailedMessage(deviceName))
        }

        // --no-window implies --no-video-playback and keeps audio flowing with no SDL window
        // ever created — simpler than Windows' poll-for-HWND-then-hide approach, since this
        // scrcpy build actually supports it (confirmed against this exact binary's --help).
        let process = makeScrcpyProcess(args: ["-s", target, "--window-title=NotiLink - Audio de \(deviceName)", "--no-video", "--no-window"])
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        process.terminationHandler = { [weak self] _ in self?.handleTermination(deviceId: deviceId, kind: .audio) }

        do {
            try process.run()
        } catch {
            diagLog?.log("scrcpy audio launch failed for \(deviceId): \(error)")
            return (false, "No se pudo iniciar scrcpy: \(error.localizedDescription)")
        }

        queue.sync { audioProcesses[deviceId] = process }
        diagLog?.log("Started audio-only share for \(deviceId) -> \(target)")
        return (true, "Compartiendo audio de \(deviceName)")
    }

    // MARK: - Stop

    public func stopVideo(deviceId: String) {
        let process = queue.sync { videoProcesses.removeValue(forKey: deviceId) }
        process?.terminate()
    }

    public func stopAudio(deviceId: String) {
        let process = queue.sync { audioProcesses.removeValue(forKey: deviceId) }
        process?.terminate()
    }

    public func stopAll(deviceId: String) {
        stopVideo(deviceId: deviceId)
        stopAudio(deviceId: deviceId)
    }

    // MARK: - Single-app sharing (mirrors StartAppAsync/MonitorAppAsync/ReturnAppAsync/StopApp)

    public func startApp(deviceId: String, ip: String, deviceName: String, packageName: String, label: String,
                          settings: ScreenShareSettings, appOptions: AppShareOptions) async -> (success: Bool, message: String) {
        if isAppActive(deviceId: deviceId, packageName: packageName) {
            return (false, "\(label) ya se está compartiendo.")
        }
        guard isAvailable else { return (false, Self.notInstalledMessage) }

        let alreadyActive = isVideoActive(deviceId: deviceId) || isAudioActive(deviceId: deviceId) || !activeApps(deviceId: deviceId).isEmpty
        guard let target = await ensureConnected(ip: ip, phoneHasActiveSession: alreadyActive) else {
            return (false, Self.connectFailedMessage(deviceName))
        }

        var args = [
            "-s", target,
            "--window-title=\(label) - NotiLink (\(deviceName))",
            appOptions.displaySize.isEmpty && appOptions.dpi <= 0
                ? "--new-display"
                : "--new-display=\(appOptions.displaySize)\(appOptions.dpi > 0 ? "/\(appOptions.dpi)" : "")",
            "--start-app=\(appOptions.restartApp ? "+" : "")\(packageName)",
            "--display-ime-policy=\(appOptions.imePolicy)",
            "--video-bit-rate=\(min(max(settings.videoBitRateMbps, 1), 100))M",
        ]
        // Without decorations the virtual display shows ONLY the app (no launcher, clock, battery
        // or navigation bar); closing the app leaves the window black instead of the home screen.
        if !appOptions.decorations { args.append("--no-vd-system-decorations") }
        if appOptions.keepOnClose { args.append("--no-vd-destroy-content") }
        if !appOptions.audio { args.append("--no-audio") }
        if appOptions.alwaysOnTop { args.append("--always-on-top") }
        if appOptions.borderless { args.append("--window-borderless") }
        if settings.maxSize > 0 { args.append("--max-size=\(settings.maxSize)") }
        if settings.maxFps > 0 { args.append("--max-fps=\(settings.maxFps)") }
        if settings.videoCodec == "h265" || settings.videoCodec == "av1" { args.append("--video-codec=\(settings.videoCodec)") }
        if settings.videoBufferMs > 0 { args.append("--video-buffer=\(settings.videoBufferMs)") }

        let process = makeScrcpyProcess(args: args)
        let session = AppShareSession(target: target, deviceId: deviceId, packageName: packageName,
                                       label: label, process: process, autoReturn: appOptions.autoReturn)
        let key = Self.appKey(deviceId, packageName)

        let outPipe = Pipe()
        let errPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        process.terminationHandler = { [weak self] _ in self?.handleAppTermination(key: key, deviceId: deviceId, packageName: packageName) }

        do {
            try process.run()
        } catch {
            diagLog?.log("scrcpy app-share launch failed for \(deviceId)/\(packageName): \(error)")
            return (false, "No se pudo iniciar scrcpy: \(error.localizedDescription)")
        }

        queue.sync { appSessions[key] = session }
        attachAppDisplayScraper(outPipe, session: session)
        attachAppDisplayScraper(errPipe, session: session)
        session.monitorTask = Task { [weak self] in await self?.monitorApp(session: session) }

        diagLog?.log("Started app share for \(deviceId)/\(packageName) -> \(target)")
        return (true, "Abriendo \(label) de \(deviceName)...")
    }

    public func stopApp(deviceId: String, packageName: String) {
        let key = Self.appKey(deviceId, packageName)
        let session = queue.sync { appSessions.removeValue(forKey: key) }
        session?.monitorTask?.cancel()
        session?.process.terminate()
    }

    public func stopAllApps(deviceId: String) {
        for app in activeApps(deviceId: deviceId) {
            stopApp(deviceId: deviceId, packageName: app.packageName)
        }
    }

    /// Moves the app back onto its virtual display (the PC window) — "Traer a la PC". Mirrors
    /// `ReturnAppAsync` (C#), minus the companion-mirror window swap (not built here).
    public func returnApp(deviceId: String, packageName: String) async {
        let key = Self.appKey(deviceId, packageName)
        let maybeSession = queue.sync { appSessions[key] }
        guard let session = maybeSession, let displayId = session.displayId else { return }

        if session.launcherActivity == nil {
            let resolved = await runAdb(["-s", session.target, "shell", "cmd", "package", "resolve-activity",
                                          "--brief", "-c", "android.intent.category.LAUNCHER", packageName])
            guard let last = resolved.split(separator: "\n").map({ $0.trimmingCharacters(in: .whitespaces) }).last(where: { $0.contains("/") }) else { return }
            session.launcherActivity = last
        }
        guard let activity = session.launcherActivity else { return }

        _ = await runAdb(["-s", session.target, "shell", "am", "start", "--display", "\(displayId)",
                           "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", "-n", activity])

        let changed = queue.sync { () -> Bool in
            let wasChanged = session.state != "pc"
            session.state = "pc"
            return wasChanged
        }
        if changed { onAppStateChanged?(deviceId, packageName, "pc") }
    }

    private func handleAppTermination(key: String, deviceId: String, packageName: String) {
        let session = queue.sync { appSessions.removeValue(forKey: key) }
        session?.monitorTask?.cancel()
        diagLog?.log("App share for \(deviceId)/\(packageName) ended")
        onAppStopped?(deviceId, packageName)
    }

    private func attachAppDisplayScraper(_ pipe: Pipe, session: AppShareSession) {
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
            let range = NSRange(text.startIndex..., in: text)
            guard let match = Self.newDisplayRegex.firstMatch(in: text, range: range),
                  let r = Range(match.range(at: 1), in: text), let id = Int(text[r]) else { return }
            self?.queue.sync { session.displayId = id }
        }
    }

    /// Every ~2.5s: where is this app's task? On the virtual display -> showing on the PC. On
    /// another display -> the user opened it on the phone (PC window black). Nowhere at all
    /// (twice in a row, after it was seen) -> closed from the phone's Recents, so the share is
    /// stopped. Mirrors `MonitorAppAsync` (C#), minus starting/stopping the companion mirror.
    private func monitorApp(session: AppShareSession) async {
        var seen = false
        var misses = 0
        let escapedPackage = NSRegularExpression.escapedPattern(for: session.packageName)
        let taskRegex = try! NSRegularExpression(pattern: #"^  \* Task\{.*(?:A=\d+:"# + escapedPackage + #"(?:\s|$)|I="# + escapedPackage + #"/)"#)

        while !Task.isCancelled && session.process.isRunning {
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            if Task.isCancelled || !session.process.isRunning { return }

            let maybeDisplay = queue.sync { session.displayId }
            guard let ourDisplay = maybeDisplay else { continue }

            let dump = await runAdb(["-s", session.target, "shell",
                                      "dumpsys activity activities | grep -E '^Display #|^  \\* Task\\{'"])
            guard !dump.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }

            var currentDisplay: Int?
            var foundOn: Int?
            var visible = false
            for rawLine in dump.split(separator: "\n", omittingEmptySubsequences: false) {
                let line = String(rawLine).trimmingCharacters(in: CharacterSet(charactersIn: "\r"))
                let fullRange = NSRange(line.startIndex..., in: line)
                if let h = Self.displayHeaderRegex.firstMatch(in: line, range: fullRange), let r = Range(h.range(at: 1), in: line) {
                    currentDisplay = Int(line[r])
                    continue
                }
                if let current = currentDisplay, taskRegex.firstMatch(in: line, range: fullRange) != nil {
                    foundOn = current
                    visible = line.contains("visible=true")
                    if foundOn == ourDisplay { break }
                }
            }

            guard let foundOn else {
                if seen {
                    misses += 1
                    if misses >= 2 {
                        diagLog?.log("app share: \(session.packageName) closed on the phone, stopping")
                        stopApp(deviceId: session.deviceId, packageName: session.packageName)
                        return
                    }
                }
                continue
            }
            misses = 0
            seen = true

            let state = foundOn == ourDisplay ? "pc" : "phone"
            let changed = queue.sync { () -> Bool in
                let wasChanged = session.state != state
                session.state = state
                return wasChanged
            }
            if changed { onAppStateChanged?(session.deviceId, session.packageName, state) }

            if state == "phone" && !visible && session.autoReturn {
                await returnApp(deviceId: session.deviceId, packageName: session.packageName)
            }
        }
    }

    // MARK: - Process plumbing

    private func makeScrcpyProcess(args: [String]) -> Process {
        let process = Process()
        process.executableURL = scrcpyPath
        process.arguments = args
        process.currentDirectoryURL = toolsDir
        var env = ProcessInfo.processInfo.environment
        env["ADB"] = adbPath.path // documented in `scrcpy --help` as the way to point at a specific adb
        process.environment = env
        return process
    }

    private func handleTermination(deviceId: String, kind: ScreenShareKind) {
        queue.sync {
            switch kind {
            case .video: videoProcesses.removeValue(forKey: deviceId)
            case .audio: audioProcesses.removeValue(forKey: deviceId)
            }
        }
        diagLog?.log("scrcpy \(kind) share for \(deviceId) ended")
        onStopped?(deviceId, kind)
    }

    private func attachOutputScraper(_ pipe: Pipe, deviceId: String) {
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty, let text = String(data: data, encoding: .utf8) else { return }
            self?.scrapeStats(text, deviceId: deviceId)
        }
    }

    private func scrapeStats(_ text: String, deviceId: String) {
        let range = NSRange(text.startIndex..., in: text)
        var fps: Double?
        if let match = Self.fpsRegex.firstMatch(in: text, range: range), let r = Range(match.range(at: 1), in: text) {
            fps = Double(text[r])
        }

        var resolution: String?
        if let match = Self.resolutionRegex.firstMatch(in: text, range: range), let r = Range(match.range(at: 1), in: text) {
            resolution = String(text[r])
            queue.sync { lastResolution[deviceId] = resolution }
        }

        guard fps != nil || resolution != nil else { return }
        let stickyResolution = resolution ?? queue.sync { lastResolution[deviceId] }
        onStatsUpdated?(deviceId, ScreenShareStats(fps: fps, resolution: stickyResolution))
    }

    // MARK: - ADB wireless/USB connection (mirrors EnsureConnectedAsync in ScreenShareService.cs)

    private func ensureConnected(ip: String, phoneHasActiveSession: Bool) async -> String? {
        let target = "\(ip):\(Self.wirelessPort)"

        let devicesOutput = await runAdb(["devices"])
        let onlineSerials = Self.parseAdbDevices(devicesOutput)
        if onlineSerials.contains(target) { return target }

        for serial in onlineSerials where !serial.contains(":") {
            let addrOutput = await runAdb(["-s", serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0"])
            guard addrOutput.contains("inet \(ip)/") else { continue }

            // Don't restart adbd on a phone that already has an active session on another
            // feature — that would kill it. Use the existing USB connection instead.
            if phoneHasActiveSession { return serial }

            _ = await runAdb(["-s", serial, "tcpip", "\(Self.wirelessPort)"])
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            let connectOutput = await runAdb(["connect", target])
            if connectOutput.contains("connected") && !connectOutput.contains("cannot") { return target }
            return serial // fall back to USB if the wireless switch didn't take
        }

        // No matching USB device at all — try a direct wireless connect (works if "Depuración
        // inalámbrica" was already paired earlier).
        let connectOutput = await runAdb(["connect", target])
        if connectOutput.contains("connected") && !connectOutput.contains("cannot") { return target }
        return nil
    }

    private static func parseAdbDevices(_ output: String) -> [String] {
        output.split(separator: "\n").dropFirst().compactMap { line -> String? in
            let parts = line.split(separator: "\t").map(String.init)
            guard parts.count == 2, parts[1] == "device" else { return nil }
            return parts[0]
        }
    }

    private func runAdb(_ args: [String]) async -> String {
        await withCheckedContinuation { continuation in
            let process = Process()
            process.executableURL = adbPath
            process.arguments = args
            process.currentDirectoryURL = toolsDir
            let pipe = Pipe()
            process.standardOutput = pipe
            process.standardError = pipe
            process.terminationHandler = { _ in
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                continuation.resume(returning: String(data: data, encoding: .utf8) ?? "")
            }
            do {
                try process.run()
            } catch {
                continuation.resume(returning: "")
            }
        }
    }
}
