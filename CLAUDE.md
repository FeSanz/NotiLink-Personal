# NotiLink

Mirrors Android phone notifications (and a handful of remote actions) to a Windows PC over the
local network — no cloud, no external server, no dependency on Microsoft's Phone Link/Cross
Device Service infrastructure. Built from scratch specifically because Phone Link stopped
working reliably for the user and existing alternatives (KDE Connect, Pushbullet) didn't give
the exact visual fidelity or control wanted (native-style cards with images/emoji, per-app
filtering, live battery/Wi-Fi/cellular status, real delete/action-button parity with the
phone).

Two apps talk to each other over a custom TCP protocol on the LAN:

- **`android/`** — native Kotlin app. Reads notifications via `NotificationListenerService`,
  forwards them (and battery/Wi-Fi/cellular/wallpaper/media-session/call-state info) to the PC,
  and executes commands the PC sends back (delete, invoke a real action, disconnect, ring the
  phone, start/stop screen or audio sharing, open a notification via `NotiLinkAccessibilityService`).
- **`windows/`** — WPF (.NET 8) app. Runs the TCP server, renders everything in a WebView2 page
  (`windows/NotiLink.App/wwwroot/`), and is the "remote control" side — every action you take
  on the PC (delete, reply, mute, disconnect, rename, etc.) round-trips to the phone and
  actually happens there, not just visually in the PC's list.

**Read `docs/PROTOCOL.md` first if you're touching anything that crosses the wire.** It's the
canonical spec both sides implement against — if the code and that document disagree, the
document wins and the code has a bug. Keep it in sync with any protocol change.

## Architecture at a glance

```
android/app/src/main/kotlin/com/notilink/app/
├── listener/    NotificationListenerService (the whole mirroring pipeline lives here),
│                the persistent status notification + its action receivers (connection
│                toggle, screen/audio share request + stop, clipboard copy, status dismiss),
│                NotiLinkAccessibilityService (opens a notification/media app on request)
├── net/         TCP client (NotificationConnection), UDP discovery, wire framing (Protocol.kt),
│                per-device connection state (ConnectionStatus), wallpaper capture,
│                system-wide media session watching (MediaSessionWatcher) for the "now playing"
│                bar *and* call-state detection (its own Telecom-session trick, see below)
├── filter/      Per-app allow-list (AppFilterStore), installed-app enumeration
│                (InstalledAppsProvider, walks every user profile for Dual Messenger support),
│                composite app identity for cloned apps (AppIdentity)
├── pairing/     Device identity, pairing token storage, discovery client, pairing UI state
└── ui/          MainActivity (onboarding/pairing/settings), AppFilterActivity

windows/
├── NotiLink.Core/         Framework-agnostic: wire framing, TcpServer, UDP discovery responder,
│                          DPAPI-encrypted paired-device store, window-placement store, models,
│                          PeerLinkService (PC ↔ PC link) + its stores
├── NotiLink.App/          WPF shell + WebView2 host. MainWindow.xaml.cs is the JS↔C# bridge;
│   │                      MainViewModel.cs routes TcpServer's wire events to UI-facing events.
│   ├── Services/          ScreenShareService (scrcpy process wrapper), AudioLevelMonitor (real
│   │                      WASAPI per-process peak metering, via the NAudio package), PcMediaService
│   │                      (this PC's own local media session via Windows SMTC), AppInfo
│   └── wwwroot/           The entire UI — app.js/app.css/index.html. WPF just hosts this page;
│                          there is no other native UI beyond the tray icon.
└── NotiLink.Tests/        xUnit tests for wire framing
```

### Wire protocol essentials (full spec: `docs/PROTOCOL.md`)

- Custom TCP framing: `[4-byte big-endian length][1-byte type][payload]`. Type `0x01` = JSON
  envelope `{v, type, id, ts, payload}`; type `0x02` = binary blob, always immediately following
  the JSON message that referenced it via a `transferId`.
- **PC is always the TCP server** (port 45212), phone is always the client — Android background
  processes are unreliable inbound listeners under Doze/OEM restrictions, so the always-on
  server has to live on the PC.
- UDP discovery on port 45211 (phone broadcasts, PC replies) with mandatory manual-IP fallback,
  since broadcast is silently dropped on some networks.
- Pairing: `handshake.hello` always first frame → if token invalid, 6-digit `pair.code` shown
  on both screens → `pair.confirm` → PC issues + persists a `pair.token` (DPAPI on PC, Android
  Keystore-backed `EncryptedSharedPreferences` on phone).
- **Multi-device**: `TcpServer` keys connections by `deviceId` (from each connection's first
  `handshake.hello`), not by a single global stream — every event and every `SendAsync` is
  scoped to one specific phone. The WebView2 page keeps a completely separate state bucket
  (notifications/apps/battery/wifi/wallpaper/media) per `deviceId` and renders only the one currently
  selected in the device switcher. General rendering *settings* (font size, theme, card radius,
  etc.) are the one thing that stays global — they live in the page's own `localStorage`.
- **Dual Messenger / cloned apps**: a cloned second instance of an app shares the same Android
  package name, just a different user profile. Everywhere an app is identified on the wire
  (`packageName` field in `notification.posted`/`app.sync`/`app.filter.update`/`icon.*`), it's
  actually a **composite identity** from `AppIdentity.kt` — bare package name for the primary
  profile, `"pkg#profileUserId"` for a clone — so the two get fully independent filter state and
  show as separate entries. Android's `LauncherApps` cross-profile enumeration is restricted for
  non-launcher apps, so the clone may not show up in the Apps filter list *ahead of time*, but
  if its notification reaches the listener it still gets mirrored (default-allowed) under its
  own identity.

## Key design decisions worth knowing before you change things

- **WebView2, not native WPF controls, for the whole UI.** Chosen specifically for free native
  color-emoji rendering and CSS-based glass/blur effects that WPF's own rendering can't do well.
  This means almost all UI work happens in `wwwroot/app.js`/`app.css`/`index.html`, not XAML.
  `MainWindow.xaml.cs` is deliberately thin — just the bridge (`window.chrome.webview.postMessage`
  JS→C#, `ExecuteScriptAsync` C#→JS) and non-UI OS integration (tray icon, window placement,
  DPI manifest, dark-title-bar P/Invoke).
- **Every PC-side action actually happens on the phone, never just visually.** Delete cancels
  the real notification; action buttons (reply, mark-as-read, etc.) fire the notification's own
  real `PendingIntent`/`Notification.Action` — nothing invented. "Delete all" iterates and
  cancels only notifications this app actually mirrored, never the OS-level `cancelAll()` (that
  would also kill unrelated system notifications like ongoing music/nav).
  "Silenciar"/mute-style actions are the one exception — they don't invoke reliably from the PC
  side, so they're detected by title regex and demoted to a small hover icon instead of the
  normal action row, rather than presented as if they'd work normally.
- **No TLS.** The pairing token + 6-digit code is the only trust mechanism — an accepted
  trade-off for a home-LAN MVP, not an oversight (see PROTOCOL.md's "out of scope" section).
- **Foreground service + a real notification icon, not just background execution.** The
  Android listener promotes itself to a `connectedDevice`-type foreground service so it survives
  Doze; this requires holding one of a specific permission set (we use `CHANGE_WIFI_STATE`) or
  Android throws `MissingForegroundServiceTypeException` at `startForeground()` — this exact
  crash is what caused a "NotiLink sigue fallando" loop earlier in development. The persistent
  status notification is deliberately real infrastructure (not decorative): it doubles as the
  Doze exemption *and* gives a Desconectar/Conectar toggle, and respects being manually
  dismissed (won't silently repost until the connection state genuinely changes or the app
  reopens).
- **Samsung's "sleeping apps" background limiter is a separate thing from Android's standard
  battery-optimization exemption**, and has no public API to jump straight to it — if
  reconnection only works while the app is in the foreground, that's very likely this, not a
  bug in NotiLink. There's a button in the app pointing at the app's own system settings page as
  the closest available deep link; the rest is manual (Batería > Límites de uso en segundo
  plano > Apps que nunca duermen).
- **Every "optional" phone capability degrades gracefully, never crashes.** Wallpaper capture,
  Wi-Fi SSID/signal, and cellular signal/carrier all require runtime permissions
  (`READ_EXTERNAL_STORAGE`/`READ_MEDIA_IMAGES`, `ACCESS_FINE_LOCATION`, `READ_PHONE_STATE`
  respectively) that the user may deny — the corresponding payload fields just come back
  `null`/empty in that case. These permissions are requested **sequentially, chained through
  each launcher's callback**, not fired back-to-back in `onCreate()` — Android has been observed
  to silently drop a second `ActivityResultLauncher.launch()` call if it's fired while a prior
  one's system dialog is still being set up.
- **`--card-bg` vs `--bar-bg` in `app.css` — two different translucency layers, don't conflate
  them.** `--card-bg` is the wallpaper-friendly one (`.card`, `#list`, `.now-playing-bar` — things
  that should let the phone's wallpaper glow show through). `--bar-bg` is the opaque one (`#header`,
  `.device-segment-group` — thin bars that should read crisp/solid, not translucent). These used to
  be the same variable; bumping its opacity to fix the header's washed-out look in light mode also
  raised `#list`'s opacity and nearly blocked the wallpaper entirely (and silently capped the
  wallpaper-intensity slider's visible range well below 100%, since a near-opaque glass layer sits
  between the wallpaper and the eye regardless of the wallpaper's own opacity). If a card ever needs
  to look crisper, bump `--bar-bg` usage there, not `--card-bg` globally.
- **`MediaSessionWatcher` registers a callback on *every* candidate session, not just the one
  currently shown.** Confirmed bug from only watching the active one: switching from YouTube to
  Spotify while YouTube was the displayed session never updated the PC's bar, because nothing was
  listening to Spotify's own state change — `OnActiveSessionsChangedListener` only fires on a
  session being created/destroyed, not on an already-known session's playback state changing.
  `syncWatchedControllers` keeps one `MediaController.Callback` per session in `getActiveSessions()`
  at all times so any of them changing state triggers a fresh re-pick.
- **Every `pickController` outcome goes through a single 200ms debounce (`schedulePublish`/
  `publishRunnable`) before actually reaching the PC.** A single play/pause/skip tap makes Spotify
  fire several callbacks back to back, and at least one in-between pass could catch a momentarily
  inconsistent snapshot (confirmed: album art briefly blank, or the bar flickering to "hidden" and
  back). Only the state still current after a short quiet window gets published — this applies to
  *both* "show new state" and "hide, nothing's playing", which used to be two separate code paths
  with only one of them debounced.
- **scrcpy runs as its own OS process, not inside NotiLink's app UID — its battery/data usage will
  not show up under "NotiLink" in Android's own per-app usage screens.** scrcpy's on-device capture
  server is pushed and launched via ADB shell, so Android attributes its resource cost to the
  ADB/shell identity, not to the NotiLink app. Everything else the app does (notification listening,
  the TCP connection, media session watching, wallpaper sync) *does* correctly attribute to NotiLink,
  since that all runs inside the app's own process — only screen/audio sharing specifically is the
  exception. Relevant if anyone (the user included) ever tries to measure NotiLink's own consumption
  and wonders why a screen-share session's cost isn't reflected there.
- **The phone can be linked to several PCs but is connected to exactly one** (it has a single
  socket). `PcSettings.savedPcs()` keeps every PC (id = its `pcId` from the discovery announce, or
  an `"ip:<addr>"` placeholder until known — older single-PC installs are migrated to one such
  entry), `activePcId` is the one `pc_ip`/`pc_addresses` point at; switching goes through
  `PcSettings.activate` + `NotificationConnection.reconnectTo`. Tokens for all of them are already
  sent in every `handshake.hello` (`pairingTokens`), so going back to an earlier PC needs no code.
  **`NotificationConnection.rediscover` only ever follows the active PC** (by id; an id-less active
  PC only when it's the only one answering) — it used to take whichever PC answered first, which
  with two NotiLink PCs on the LAN could silently move the phone to the wrong one.
  `MainActivity`'s connection card checks before acting (`DiscoveryClient.probe`: unicast discovery,
  then a bare TCP connect) and reports the result in a toast; Reconectar only exists with a saved PC.
- **Close ≠ quit on the PC app.** Closing the window (X, Alt+F4) hides it and keeps everything
  running in the background exactly as if it were open — same behavior as Discord/Spotify.
  Only the system tray icon's "Salir" menu item actually exits (`_isExiting` flag in
  `MainWindow.xaml.cs` distinguishes the two paths through the same `Closing` handler). The tray
  icon uses WinForms' `NotifyIcon` (`UseWindowsForms` alongside `UseWPF` in the csproj) — note
  `Assembly.Location` returns empty in a single-file publish, so the tray icon is loaded via
  `Environment.ProcessPath` instead, not `Assembly.GetExecutingAssembly().Location`.
- **DiagLog files live at `%LocalAppData%\NotiLink\logs\{deviceId}.log`** — keyed by deviceId
  *alone*, not deviceId+deviceName. An earlier version included the current device name in the
  filename, so every rename silently orphaned the previous file instead of updating it, leaving a
  pile of stale same-device logs with older timestamps sitting next to the current one — easy to
  open by mistake via the "Abrir carpeta de logs" button, and easy to misdiagnose as "logs aren't
  arriving" when they actually are, just under a different filename. `MainViewModel.WriteDiagLog`
  now also deletes any leftover `*_{deviceId}.log` files on every write.

## Notable bugs fixed (root causes worth knowing if something similar reappears)

- **Resync-on-reconnect used to be tied to whichever `send()` call happened to notice the dead
  socket, not to reconnection itself.** `NotificationConnection.sendWithImages`'s per-call
  `onFreshConnect` callback only fires for the *one specific* send that discovers the old socket is
  dead and repairs it — if some other, resync-less caller (e.g. a battery-change broadcast) won the
  race against the next heartbeat tick, the reconnect happened silently and no resync ever ran for
  that connection's entire lifetime. Symptom: after a reconnect, the PC's notification list stayed
  empty until a genuinely new notification arrived on its own. Fixed by adding
  `NotificationConnection.onConnectionEstablished`, a durable global hook set once in
  `onListenerConnected()` that fires on *every* successful `connect()` regardless of which caller
  triggered it — resync-on-reconnect must always be wired to this, never to one call's own
  completion callback.
- **A device's custom name could silently reset to the phone's raw default.** `HandlePairConfirm`'s
  `FindByDeviceModel` lookup deliberately *excludes* the deviceId currently pairing (it exists to
  recognize a *different* deviceId as the same physical phone, e.g. after an uninstall/reinstall) —
  so if `HandleHandshake`'s token check ever fails for a phone that's genuinely still the same,
  already-known device (confirmed happening in practice, exact trigger not fully root-caused),
  the fresh `pair.confirm` that followed had nothing to carry the existing name over from, and fell
  back to the phone's generic default. Fixed by checking this exact deviceId's own existing store
  entry *first*, before falling back to the cross-deviceId model search — preserves the name in
  both scenarios regardless of why the token check failed.
- **A phone that silently vanishes from the network (left WiFi range, airplane mode, ...) was
  never actually detected as disconnected, on either side.** `TcpServer.DeviceDisconnected` only
  ever fired from a real socket-level I/O error — a blocking `ReadAsync()` with no incoming data
  and no TCP FIN/RST just hangs, for as long as the OS is willing to wait on a dead socket with no
  keepalive configured (potentially a very long time). The 45s "mark offline" heartbeat timeout
  `docs/PROTOCOL.md` had always *described* was never actually implemented anywhere in
  `TcpServer.cs` — pure aspirational documentation. Confirmed live: a phone that left WiFi
  mid-audio-share kept scrcpy's phone-side `AudioPlaybackCapture` session stuck open, silencing
  the phone system-wide, until it was rebooted — nothing on the PC ever noticed the phone was gone,
  so nothing ever told that session to stop. Fixed with a real watchdog timer in `TcpServer`
  (`_lastSeenUtc` per device, checked every 10s, force-closes anything silent for 45s+, which then
  flows through the exact same `DeviceDisconnected` cleanup path a normal disconnect takes) plus
  the mirror fix on Android (`NotificationConnection`'s socket used `soTimeout = 0`, i.e. also
  indefinite — now 60s, with the read thread's `finally` block closing the socket on ANY exit
  reason so a stale half-dead connection is never left for some future `send()` to discover). See
  "Reconnect robustness" below for the full picture, including why the Android app itself
  *cannot* be the one to kill scrcpy's own phone-side process even if it wanted to.
- **A single transient decrypt/IO failure reading `paired-devices.dat` silently wiped the entire
  pairing store for that one request**, not just the one device being checked — `PairedDeviceStore
  .Load()`'s catch block treated any `IOException`/`CryptographicException` (file momentarily
  locked by AV scanning, DPAPI's user master key not fully available yet under boot-time
  contention, etc.) identically to "no pairings ever existed", returning an empty dictionary.
  Confirmed as a real, repeatable complaint tied specifically to the PC *rebooting* (not just
  reopening the app) — this app auto-starts from the Startup folder, so a phone reconnecting
  within the first second or two of logon can race exactly this kind of transient condition. Fixed
  with a short retry (3 attempts, 200ms apart) for `IOException`/`CryptographicException`
  specifically — a genuine `JsonException` (successfully decrypted but not valid JSON) is *not*
  retried, since that's actually-corrupt data, not a timing issue, and still falls back to empty
  immediately.

## Known rough edges / accepted limitations

- Bluetooth cannot be toggled remotely — Google removed apps' ability to silently
  enable/disable Bluetooth as of API 33. Not a bug, just no longer possible on modern Android.
- "Reconectar" only exists as a phone-initiated action. Once the PC closes a connection there's
  no channel left to remotely tell that phone to reconnect — only the phone's own persistent
  notification (or reopening the app) can do that.
- Dual Messenger clone detection is best-effort (see above) — the clone's notifications get
  mirrored correctly once they arrive, but the Apps filter screen may not be able to *list* the
  clone ahead of time on all OEMs, due to `LauncherApps` cross-profile restrictions for
  non-launcher apps.
- No real auto-update feed. `windows/publish.ps1` republishes over the same stable install path
  (`%LocalAppData%\Programs\NotiLink\`) — good enough for a single personal machine; shipping to
  other people would need real distribution infrastructure (MSIX/Velopack + a release feed),
  deliberately not built since there's currently exactly one user.
- Link previews (opt-in setting, off by default) fetch the raw HTML of any URL found in a
  notification directly from the PC via `HttpClient` and regex-extract `<title>`/OpenGraph tags
  — no headless rendering, no JS execution, capped read size, short timeout, in-memory cache by
  URL. This is a real outbound network request to whatever the phone's notifications mention;
  that's exactly why it defaults off.
- **"Silenciar"/mute-style actions the way the phone's own dialer classifies them are the one
  action type not confirmed reliable from the PC** — flagged by the user as sometimes only
  working while the corresponding app (WhatsApp) is in the foreground on the phone, not from any
  screen. Leading hypothesis, not yet confirmed against a real log: the PC's cached notification
  `key` can go stale if the app replaces/updates that notification when its own foreground state
  changes (e.g. marking a conversation read on open), and invoking a stale key silently no-ops —
  same general class of problem as the BAL/notification-open issue below, just for actions
  instead of the content intent. Needs a DiagLog capture of an actual failure to root-cause
  further; don't guess a fix without one, per this project's usual pattern.

## Build & run

**Android** (from `android/`):
```
./gradlew.bat :app:installDebug
```
Needs a device/emulator connected via adb. First run requires manually granting Notification
Access (deep-linked from the app) — there's no completion callback for that specific settings
screen, so the app re-checks on every `onResume()`.

**Windows** (from `windows/`):
```
dotnet build NotiLink.sln
```
Windows 11's Smart App Control blocks unsigned locally-built exes — see `docs/DEV_SETUP.md` for
the one-time local dev-certificate setup. The build auto-signs on every build via `sign.ps1`
(an MSBuild post-build target in `NotiLink.App.csproj`); without the cert this degrades to a
warning, not a build failure, but the app won't launch until it's signed.

Run tests: `dotnet test NotiLink.sln` (Core protocol-framing tests only so far).

## Packaging / installing an update (Windows)

There's no installer to click through — `windows/publish.ps1`:
1. `dotnet publish`s a self-contained, single-file `win-x64` build.
2. Signs it with the same local dev certificate as the debug build.
3. Installs it to `%LocalAppData%\Programs\NotiLink\` (same path every time — this *is* the
   update mechanism, no uninstall step needed).
4. (Re)creates a Start Menu shortcut and a Startup-folder shortcut, so it launches automatically
   at sign-in. Disabling that later is a normal Windows thing (Task Manager > Startup Apps),
   nothing on the app's side depends on it being enabled.

**Migrating to a different PC** (using the app there, not developing it) doesn't need the source
tree or any dev toolchain at all — `dist/NotiLink-Portable.zip` (built ad hoc, not checked in;
`dist/` is gitignored, ~180MB uncompressed) bundles the already-published+signed app, the vendored
scrcpy, and the public half of the local code-signing certificate (`NotiLink-Dev-Signing.cer`, no
private key — safe to distribute, only lets Windows trust binaries already signed with it). Its own
`instalar.ps1` trusts the certificate on the new machine (same mechanism as `docs/DEV_SETUP.md`,
just scripted) and installs to the same stable path. Paired devices don't travel with it — the
pairing token is DPAPI-encrypted to the old PC's Windows account, so the phone re-pairs from
scratch on the new machine, same as a first-time setup.

**Wider distribution plan (not yet acted on)**: GitHub Releases for the portable PC zip (versioned,
native to the repo, no link-rot the way a shared Drive link can have) and GitHub Pages for
documentation — both free, no separate site to host, matching the explicit "no quiero pagar
alojamiento propio" constraint. `docs/PRIVACY_POLICY.md` is already written and ready to publish
via Pages (required for the Play Store submission below — Play needs a real public URL, not a
repo file). `docs/PLAY_STORE_SUBMISSION.md` has a full checklist plus pre-drafted justification
text for the two Play Console declaration forms that scrutinize this app hardest — Accessibility
Service and Notification Access — along with what to expect if Google asks a follow-up question.

## Screen & audio sharing (scrcpy) — fully built, both directions

**scrcpy** (github.com/Genymobile/scrcpy, Apache 2.0) drives an Android phone over ADB, wrapped
as an external process by `ScreenShareService.cs` (no C# scrcpy library exists; wrapping the real
binary is the only realistic approach). Confirmed working **wirelessly over LAN**, not just USB —
`ScreenShareService` opportunistically flips a USB-connected phone into `adb tcpip 5555` mode,
then `adb connect <phone-ip>:5555`, before launching scrcpy against that target.

- `windows/tools/fetch-scrcpy.ps1` downloads the official win64 release, verifies its SHA256
  against a pinned hash, and extracts it to `windows/NotiLink.App/tools/scrcpy/` — gitignored
  (~25MB of prebuilt binaries, no place in source control), so run this once after a fresh
  clone. Self-contained: scrcpy.exe + its own adb.exe + all required DLLs, nothing else needed.
- **Current version: scrcpy 5.0.1 with adb 37.0.1** (protocol 1.0.41) — the same adb as Prisma
  (the user's scrcpy GUI), so both apps share one adb server instead of restarting each other's.
  To bump it: change `$Version`/`$Sha256` in the script (hash from the release's SHA256SUMS.txt or
  the GitHub API asset `digest`), delete `tools/scrcpy/`, re-run the script, then `publish.ps1`.
  The DLL names change between FFmpeg versions (5.0.1: avcodec-63, avformat-63, avutil-61,
  swresample-7); nothing references them by name — the csproj copies `tools\scrcpy\**` whole.
  5.0 added hardware decoding on the PC, on by default (`--hwdec=auto`); NotiLink doesn't pass it.
  If video looks wrong on some PC, `--hwdec=disabled` goes back to software decoding.
- The render buffer option is `--video-buffer` (the old `--display-buffer` name isn't in 4.1/5.0's help).
- **`ExcludeFromSingleFile` is required on the `tools\scrcpy\**` `Content` item in
  `NotiLink.App.csproj`.** Without it, `dotnet publish`'s single-file mode (specifically
  `IncludeNativeLibrariesForSelfExtract=true`, set in `publish.ps1`) sniffs every published file's
  PE header and bundles anything that looks like a native binary — every `.exe`/`.dll` here,
  `scrcpy-server` included despite its missing extension — into the single-file exe, self-extracting
  them to a hidden per-run temp folder instead of leaving them loose next to it, which broke
  "Compartir pantalla" silently (`IsAvailable` checks `File.Exists` next to the exe). A plain
  `dotnet build` never showed this — only a real `publish.ps1` run did.
- **Video mirroring** ("Compartir pantalla" in the header): quality/behavior fully parametrized
  (max resolution, max fps, video bitrate, codec h264/h265/av1, render buffer ms, turn phone
  screen off, keep phone awake, forward phone audio) via a confirmation popup that edits
  `app.js`'s `settings.screen*` fields — never hardcoded, and reused as-is by phone-initiated
  shares (see below). `--print-fps` + regex-scraped stdout/stderr drives a live fps/resolution
  readout next to the header icon. `CreateNoWindow=true` on the `Process` suppresses the console
  scrcpy.exe would otherwise pop up (it's a console-subsystem binary) — the actual SDL video
  window is unaffected by that flag. Its title already reads "NotiLink - {device}"
  (`--window-title`); its *icon* is set to NotiLink's own (extracted from the running exe itself
  via `System.Drawing.Icon.ExtractAssociatedIcon` + `WM_SETICON`, not a scrcpy flag — none exists)
  once the window appears, so the mirror window reads as belonging to NotiLink, not to scrcpy.
- **Audio-only mirroring** ("Compartir audio", a separate header icon+process from video): scrcpy
  launched with `--no-video` (audio forwarding is on by default when video isn't disabled). scrcpy
  still opens a small window for this even with no video — confirmed directly, there's no CLI flag
  to suppress it — so `ScreenShareService` polls for the window's HWND after `Process.Start()` and
  hides it via a raw `ShowWindow(hwnd, SW_HIDE)` P/Invoke once it exists. The header's own
  solid-purple-when-active icon is what represents this session's state instead of a window to
  look at or close.
  - **This almost certainly does NOT work for actual phone call audio.** scrcpy's audio capture
    uses Android's `AudioPlaybackCapture` API (Android 10+), which by platform-level privacy
    policy excludes `USAGE_VOICE_COMMUNICATION` streams specifically — apps can't silently capture
    call audio through this mechanism. Music/video/game/app-sound forwarding should work fine;
    routing an actual phone call through the PC almost certainly will not, no matter how this
    feature is configured. Flagged to the user before they tested against a real call; not yet
    confirmed either way against one.
  - It is one-directional only: phone audio *output* → PC speakers. The PC's microphone is never
    redirected to the phone — for an actual two-way call, the phone's own mic is still what picks
    up the user's voice.
- **Phone-initiated sharing**: the persistent "Conectado a la PC" notification has "🖥️ Compartir
  pantalla"/"🔊 Compartir audio" actions (`ShareActionReceiver`, mirroring `ConnectionActionReceiver`'s
  explicit-component-PendingIntent pattern) that send `share.request` to the PC. The PC bounces this
  into JS (`window.onPhoneShareRequest`) rather than handling it purely in C#, specifically so a
  phone-initiated screen share reuses the same saved `settings.screen*` values the confirmation
  popup would otherwise show — the settings live in the page's `localStorage`, not anywhere on the
  C# side, and re-deriving them there would mean two sources of truth. Screen share started this
  way skips the confirmation popup entirely (walking over to the PC to click "Iniciar" would defeat
  the point of asking from the phone).
- **Sharing-status notification on the phone**: separate from the always-on connection-status one,
  shown only while something's active (`share.status`, PC → phone, `{screenSharing, audioSharing}`),
  with one "⏹ Detener" action that stops whatever's actually running — `ScreenShareService.StopVideo()`
  and `StopAudio()` are independently tracked (`_videoProcess`/`_audioProcess`), and the phone-side
  stop always calls both, unlike the PC's own audio toggle button which only ever touches its own
  process. Only one notification ever shows even if both are active — the phone doesn't distinguish
  "screen share's own bundled audio" from "the separate audio-only feature", just whether each of
  the two underlying scrcpy processes is currently running.
- Two real UX wrinkles inherent to relying on ADB, not fixed by any of the above:
  1. A phone resets back to USB-only ADB mode on every reboot unless wireless debugging is paired
     the Android-11+ way (Settings > Developer options > Wireless debugging, one-time pairing
     code) — `adb tcpip` alone needs a USB tap each session.
  2. This is a materially different trust model than the rest of NotiLink, which otherwise never
     requires Developer Options/ADB — worth being explicit with the user that this feature means
     leaving USB/wireless debugging on, not just another notification-style permission toggle.

See `docs/PROTOCOL.md`'s `share.request`/`share.stop.request`/`share.status` for the exact wire
messages, and `media.state`/`media.control` for the separate now-playing bar below.

Run any change live:
```powershell
powershell -File windows\publish.ps1
```

## Reconnect robustness: detecting a genuinely dead connection

Both sides used to assume a connection would always fail loudly and promptly (a socket error) —
true for the phone closing the app or a clean network change, false for the specific case that
actually caused a real, confirmed problem: the phone silently leaving WiFi range with an active
screen/audio share running.

- **`TcpServer`'s watchdog** (`_lastSeenUtc`, `MaintenanceTick()`, a `System.Threading.Timer`
  ticking every 10s) force-closes any device's socket once it's gone silent without producing a
  single byte — updated on every frame received, not just parsed `heartbeat.request`s. This is
  the thing `docs/PROTOCOL.md` always described but that was never actually wired up; see the
  "Notable bugs fixed" entry above for what that gap actually caused. The timeout started at 45s
  (based on the phone's own ~8s heartbeat cadence) but that was too tight in practice — some OEMs'
  background-execution limits (Samsung's "sleeping apps"/adaptive battery in particular — see the
  rough-edge note about it) can genuinely delay a background `ScheduledExecutorService` well past
  its nominal interval without the connection itself being unhealthy, confirmed live as a real
  "se conecta y desconecta a cada rato" regression. Now 3 minutes — this watchdog only exists to
  catch a phone that's genuinely, permanently gone, and doesn't need to be tight to do that; it
  just needs to eventually fire instead of literally never.
- **`MainViewModel.DeviceDisconnected`** now calls `StopAllSharing()` whenever the device that just
  dropped is `_sharingDeviceId` — a disconnect (watchdog-triggered or real) always tears down any
  active scrcpy process for that phone, exactly like the phone's own "⏹ Detener" action would.
- **`MainViewModel.DeviceConnected`** now always re-announces this phone's *real* current share
  state (`share.status`) on every (re)connect, not just on a change — a `StopAllSharing()`-issued
  "stopped" status has nowhere to go while the phone is actually offline, so without this the
  phone's own "Compartiendo..." status notification could stay stuck showing stale state
  indefinitely after reconnecting.
- **Android's own socket had the identical bug, mirrored**: `NotificationConnection`'s
  `soTimeout` was `0` (indefinite) rather than any bounded value, and the read loop's exit path
  never explicitly closed the socket — so even after this app's own connection to the PC died,
  nothing forced a clean state transition; a later `send()`'s write-failure detection was the only
  path back to `connect()`, and writes to an already-locally-dead-but-not-yet-detected socket can
  also hang. Fixed: `soTimeout = 60_000`, and the read thread's `finally` block always calls
  `closeQuietly()` on exit (guarded by `readThread === Thread.currentThread()`, so an old thread
  finally unwinding after `connect()` already replaced it with a fresh one never clobbers current
  state).
- **That 60s Android read timeout immediately caused a real regression on its own, until fixed**:
  the PC had never actually sent anything to the phone on its own initiative — only the phone sent
  periodic heartbeats, and the PC only ever replied reactively (a command, a status push). Any 60s+
  stretch with no PC-initiated traffic at all (completely normal — no deletes/actions/volume
  changes for a minute is just... not touching anything) tripped the phone's new timeout for no
  real reason, closing an otherwise perfectly healthy connection and reconnecting — confirmed live
  as "se conecta y desconecta a cada rato" immediately after the phone-side timeout shipped, plus
  the pairing code re-appearing repeatedly on every PC reboot (more frequent reconnects meant more
  chances to land inside the transient pairing-store race described above). `TcpServer
  .MaintenanceTick` (the same 10s timer as the watchdog above) now also sends this PC's own
  `heartbeat` to every connected device on every tick — this is what actually makes
  `docs/PROTOCOL.md`'s "heartbeat both directions" description true; it wasn't, before. Both a read
  timeout AND a periodic send have to exist together on a side for that side's timeout to ever be
  safe — adding one without the other is exactly what caused this.
- **Important boundary, not fixed by any of the above**: the actual audio/screen *capture* on the
  phone is `scrcpy-server`, spawned via `adb shell` — a completely separate process under a
  different UID (`shell`), entirely outside the NotiLink Android app's own sandbox. **The mobile
  app has no OS permission to kill it directly**, no matter what's changed in its own code. The
  fix above works by having the *PC* kill its own scrcpy.exe the moment it notices the phone is
  gone (`StopAllSharing()`), which tears down that process's ADB tunnel; whether the phone-side
  `scrcpy-server` notices promptly depends on ADB/scrcpy's own connection handling, which is
  outside NotiLink's codebase (a vendored third-party tool). If audio/screen ever gets stuck again
  despite this, that's the next place to look — not the Android app itself.
- **`PairedDeviceStore.Load()` retries** transient `IOException`/`CryptographicException` a few
  times before giving up — see "Notable bugs fixed" above.
- **A stale card that can never be deleted**: if a notification gets dismissed on the phone
  *while it's genuinely disconnected* from the PC (confirmed real case — another app, e.g. Quick
  Share, briefly switching the phone's own network), that `notification.removed` has no
  connection to travel over and is simply lost — the PC card sits there forever, and clicking
  delete does nothing since the phone has no record of that key any more to confirm removal for.
  Fixed with `notification.sync` (phone → PC, sent once right after the resync burst on every
  fresh connection): the phone's complete current set of active keys, which the PC (via
  `window.syncNotificationKeys` in `app.js`) uses to remove anything it's showing that ISN'T in
  that set — except `share-`-prefixed synthetic cards, which the phone was never going to know
  about in the first place. See `docs/PROTOCOL.md`'s `notification.sync`.
- **A PC-initiated "Desconectar" could leave a phone unable to reconnect through any visible UI
  path.** Scanning a fresh QR code or using "Buscar" (`DiscoveryClient`) only ever *saved* the new
  PC IP — neither cleared `ConnectionStatus.manuallyDisconnected` nor attempted a connection, so
  if that flag was stuck `true` (set by the PC's own "Desconectar", which the phone has no way to
  un-set except locally), every subsequent send silently no-oped
  (`NotificationConnection.sendWithImages`'s own guard) no matter how many times a correct IP was
  re-provided — only the dedicated "Reconectar" button (or the status notification's own
  "Conectar" toggle) actually cleared it, and neither is obvious to reach for after "I just gave
  it the right IP, why won't it connect". Confirmed as a real report where the only recovery path
  the user found was deleting and re-pairing the device from scratch. Fixed: both the QR-scan
  result handler and the "Buscar" success callback now call `reconnectNow()` — finding a working
  PC address is an unambiguous "I want to be connected" signal, so it's now treated as one.
- **The PC side had zero persistent logging until now.** `TcpServer.Log` existed as an event, but
  nothing ever subscribed to it — every PC-side decision (why did `HandleHandshake` show the
  pairing code again for an already-paired phone? why did a device get marked stale?) was
  completely invisible after the fact, unlike the phone's own DiagLog. Fixed with
  `Services/PcDiagLog.cs` — a minimal rolling log at `%LocalAppData%\NotiLink\logs\pc.log`
  (same folder the phone's own synced logs already live in), wired to `TcpServer.Log` and to
  explicit diagnostics in `HandleHandshake` (whether a token was provided, whether a stored entry
  for that deviceId exists at all, and whether its token actually matches — the difference
  between "never paired"/"store lost track of it" and "a real mismatch"). `PairedDeviceStore`'s
  own load retry was also bumped from 3×200ms to 6×300ms while investigating this, as a cheap
  additional safety margin. If the pairing code shows up again, check `pc.log` first before
  guessing — that's the whole reason this exists.

## Now-playing media bar

Mirrors whichever media session (Spotify, YouTube, YouTube Music, ...) is currently playing —
`MediaSessionWatcher.kt` on Android, driving a persistent bar in the PC's header area (deliberately
not a notification card). Real transport controls only, nothing invented:

- **The bar is always visible, even with nothing playing** — `renderNowPlaying()` shows an
  explicit "no hay nada reproduciéndose" state with every control disabled rather than hiding the
  bar. An earlier version hid it entirely with no active session, but that made "genuinely nothing
  playing" indistinguishable from "the bar broke and disappeared" — exactly what several of the
  bugs above looked like from the outside. Don't reintroduce the hide/show toggle without a good
  reason; if you do, that ambiguity comes back.
- **Favorite/like and shuffle both go through `PlaybackState.customActions`**, not a dedicated
  platform API — there isn't one on the raw `android.media.session` classes (only the AndroidX
  compat wrapper has shuffle-mode methods, which this project doesn't depend on). Detected by
  matching an action's id/name against keywords (`favorit|me gusta|like|guardar|save|coraz[oó]n|
  heart|colecci[oó]n|library` for favorite, `shuffle|aleatorio` for shuffle) — there's no
  standardized action-id constant to match against instead, every app picks its own strings.
  Confirmed against a real device (Spotify): favorite's *name* changes with state
  ("Agregar a la colección" ↔ "Eliminar de la colección", so the current-state signal comes from
  matching "remove/quitar/eliminar" in the name), while shuffle's name stays static
  ("Activar/desactivar modo aleatorio") and its *action id* changes instead
  (`TURN_SHUFFLE_ON`/`TURN_SHUFFLE_OFF`) — the two need different detection logic for exactly
  this reason, don't assume they work the same way.
- **Album art resolution, in order**: embedded `MediaMetadata` bitmap keys (rare — Spotify and
  most modern apps return null from all of them) → `content://` URI keys resolved via
  `ContentResolver` (never `http(s)://`, that would be an outbound network fetch this app's
  LAN-only premise doesn't want) → the matching notification's own large icon as a last-resort
  fallback (usually redundant with the URI path, kept for apps that don't populate a URI at all).
  The resolved bitmap for a given URI is cached (`lastArtUri`/`lastArtBitmap`) — without this,
  publishState() was re-hitting Spotify's content provider on every single play/pause toggle (not
  just track changes), and that cross-process call occasionally failed transiently, which used to
  show as the cover flashing to blank with no discernible pattern. A *new* track's first resolution
  attempt can still fail (confirmed: the provider isn't always ready the instant new metadata
  arrives) — `scheduleArtRetry` retries up to 4 times (400ms/800ms/1200ms/1600ms) before giving up,
  checking the still-current art URI each time so a stale retry can never clobber a track change
  that happened in the meantime.
- **PC-side safety net**: if a `media.state` update arrives with no new art but is still the same
  track (same package/title/artist) as the last one shown, `MainViewModel.HandleMediaState` carries
  the previous `AlbumArtBytes` forward instead of clearing it — a second line of defense alongside
  the Android-side caching/retry, in case some other failure mode ever slips through.
- **The album art binary transfer was silently dropping for a while** —
  `TcpServer.ExtractExpectedTransferIds` recognized `icon.data`/`wallpaper.data`/`file.send`/
  `notification.posted` but never `media.state`, so its `albumArtTransferId` never got queued and
  the binary frame right after it always hit the "no pending transferId, ignoring" branch. Confirmed
  via on-device logs showing the phone successfully resolving and sending art every time
  (`hasArt=true`) while the PC never rendered it — a reminder that adding a new message type with
  its own binary follow-up needs a matching case here, not just on the sending side.
- A "ghost" media session (some OEM-preinstalled apps — confirmed on one device with Amazon Music,
  never installed — register a dormant `MediaSession` that can sit reporting a stale
  `STATE_PLAYING` with nothing loaded, indefinitely) must never outrank a real one just by being
  first in `getActiveSessions()`. `hasRealTrack()` (a non-blank title) gates every candidate before
  `STATE_PLAYING` is even considered, and nothing being genuinely usable hides the bar entirely
  rather than falling back to showing that stub.
- **Seek/scrub bar**: a native `<input type="range">` (not a hand-rolled draggable div — dragging,
  click-to-jump, and touch all come free), position/duration interpolated client-side between
  real `media.state` updates from `postedAt`/`position` (same technique the call bar's duration
  timer uses). Fires real `TransportControls.seekTo()` on release, gated on a real `canSeek` flag
  (`PlaybackState.ACTION_SEEK_TO` in the actions bitmask — never invented). **Confirmed bug**:
  `position` is deliberately excluded from `publishState`'s fingerprint dedup guard (every
  playing track would otherwise force a republish every single tick), which meant a seek's own
  resulting `onPlaybackStateChanged` was silently swallowed as "nothing changed" — the PC stayed
  frozen on the pre-seek position until some *other* field (e.g. play/pause) happened to force a
  fresh publish through. Fixed by invalidating `lastPublishedFingerprint` right after `seekTo()`,
  letting that same real callback reach the PC instead of adding an arbitrary delay/re-publish.
  The fill's "played" vs. "remaining" two-tone look is a CSS custom property (`--seek-pct`, set
  from JS on every tick) read by `::-webkit-slider-runnable-track`'s own gradient background —
  setting `.style.background` directly on the `<input>` does *not* reach that pseudo-element.
- **Smart Shuffle** (Spotify's "modo aleatorio inteligente") is the *same* custom-action slot as
  regular shuffle, confirmed from a real device log — never both at once, distinguished only by a
  `SMART_` infix in the action id (`TURN_SMART_SHUFFLE_OFF` vs. `TURN_SHUFFLE_OFF`); the
  human-readable name is identical either way, so unlike everything else here this can only be
  read from the id, never the name (`isSmartShuffle` field). The shuffle icon's own SVG swaps its
  second (crossing) arrow for a sparkle in place — not a separate corner badge — matching
  Spotify's actual icon design; only that arrow's starting stub near the sparkle is replaced, its
  ending arrowhead at the bottom-right stays visible in both modes.

## Controlling the PC's own local media (toggle on the now-playing bar)

A toggle button on the now-playing bar (`#now-playing-source-btn`, a real 3D CSS flip —
`perspective`/`rotateY`/`backface-visibility`, not a crossfade) switches the bar between
controlling the phone's media session (the existing behavior above) and controlling whatever's
currently playing on the PC itself (Spotify desktop, a browser tab, VLC, ...) — same UI, same
transport controls, just a different source/target. `nowPlayingSource` (`'phone' | 'pc'`) is a
plain global preference in `app.js`, persisted to `localStorage` — deliberately *not* per-device
state the way `deviceStates` is, since there's only one PC regardless of which phone is selected
in the switcher.

- **`Services/PcMediaService.cs`** wraps `Windows.Media.Control.
  GlobalSystemMediaTransportControlsSessionManager` — the same WinRT API that powers Windows' own
  native media flyout. Required bumping `NotiLink.App.csproj`'s TFM from plain `net8.0-windows` to
  the versioned `net8.0-windows10.0.19041.0` (`SupportedOSPlatformVersion` kept at `10.0.17763.0`
  so the rest of the app still runs on older Windows, just without this one feature) — that's what
  gets the WinRT projection for free without a separate CsWinRT/UWP interop package. "Current
  session" is whatever Windows itself currently considers active (usually whichever app most
  recently started/changed playback) — this app doesn't pick a specific app to watch, it mirrors
  the OS's own notion of "what's playing right now", exactly matching what the native flyout would
  show.
- **Favorite is genuinely not available for PC media — confirmed via research, not assumed.**
  There is no cross-app "like"/rating concept anywhere in the System Media Transport Controls
  surface any third-party app can read; the button is hidden entirely in PC mode rather than
  shown-disabled, so it doesn't look like a missing permission.
- **Shuffle IS real for PC media** (`TryChangeShuffleActiveAsync`/`Controls.IsShuffleEnabled`,
  confirmed against Microsoft's own API docs after an earlier assumption lumping it in with
  favorite turned out to be wrong) — stays visible and functional in both modes. There is, however,
  no OS-level "smart shuffle" concept the way Spotify's own app has (that's an app-internal
  distinction never communicated to the OS), so the sparkle badge never appears for PC media —
  `pcMedia` simply has no `isSmartShuffle` field, and the existing CSS/JS naturally falls back to
  plain shuffle styling.
- Seek uses `TryChangePlaybackPositionAsync` (ticks, i.e. `TimeSpan.FromMilliseconds(ms).Ticks`);
  play/pause is `TryTogglePlayPauseAsync` (a real OS-level toggle — no phone-style explicit
  play-vs-pause branching needed here). All controls are local-only (`MainViewModel
  .SendPcMediaControl`) — no phone round-trip, no network involved, since the session being
  controlled is already running on this same machine.
- Album art comes from `MediaProperties.Thumbnail` (`IRandomAccessStreamReference` →
  `OpenReadAsync()` → `AsStreamForRead()` → copied to a `byte[]`); a stale in-flight `RefreshAsync`
  call is guarded against clobbering a newer session's state by re-checking `session != _session`
  right before firing `StateChanged`, since the current session can change while those awaits are
  in flight.

## Notification card extras

- **"Copiar enlace"** — a footer pill shown only when a notification's text actually contains a
  URL (reuses `firstUrl()`, the same extraction the opt-in link-preview feature already used).
  Entirely PC-local: copies straight to the Windows clipboard via WPF's `Clipboard.SetText`, never
  round-trips to the phone as an `action.invoke` the way a real notification action would.
- **Image zoom** — the double-click detail modal's images (message stickers, big-picture,
  whatever) get a corner magnifier button (and the image itself is clickable) opening a
  full-viewport lightbox, sitting at a higher z-index than the detail modal itself so it isn't
  clipped by that modal's own `overflow-y: auto`. Mouse wheel zooms past the initial fit-to-
  screen size (up to 4×) once inside — `{ passive: false }` + `preventDefault()` on the wheel
  listener so scrolling zooms instead of trying to scroll a fixed full-viewport overlay that has
  nothing to scroll.
- **"Abrir archivo" / "Abrir ruta"** — only on the synthetic "file received from phone" card
  (`MainViewModel.HandleIncomingFile`). The file already lives on this PC's own disk (saved to
  the real Windows Downloads folder), so both are handled entirely locally: `Abrir archivo` uses
  `Process.Start` with `UseShellExecute = true` (the OS's own default handler for that file type);
  `Abrir ruta` opens Explorer with `/select,"path"` so the file itself is highlighted, not just
  the bare folder. A small `_sharedFilePaths` dictionary (notification key → absolute path) is
  what lets the PC-only click handlers find the right file — `NotificationItem.HasSavedFile`
  drives whether the two buttons render at all.
- **Progress bar** — mirrors `Notification.EXTRA_PROGRESS`/`EXTRA_PROGRESS_MAX`/
  `EXTRA_PROGRESS_INDETERMINATE`, the same standard API behind a native progress bar in Android's
  own shade (Play Store installs, Chrome downloads, WhatsApp sending a video, ...). **Confirmed
  real bug, fixed**: `hasProgress` was first computed from whether the extra *keys* were present
  at all, which showed an empty bar on nearly every notification — plenty of apps' underlying
  `Notification.Builder` path populates these with harmless defaults (`max=0`, `indeterminate=
  false`) even when `setProgress()` was never called. `progressMax > 0 || indeterminate` (checking
  the actual *value*, not key presence) is what genuinely means "this has a real progress bar" —
  a freshly-started determinate download is still `max>0` at 0%, so that case isn't missed either.
  Indeterminate (no real percentage yet, e.g. Play Store's initial "Descargando...") renders as a
  moving/pulsing sweep instead of a frozen fill.
- **Accent color** — mirrors `Notification.color` (the app's own requested accent, excluded when
  it's `COLOR_DEFAULT`/unset) as a card's left border, via a `--card-accent` CSS custom property
  set inline per-card (`cardAccentStyle` in `app.js`) rather than a static class, since it's a
  per-notification value. Most apps never request one, in which case the border just falls back to
  the same color as the rest of the card's border (`var(--card-accent, var(--card-border))`), 2px
  wider but otherwise imperceptible.
- **Quick-reply choices** — mirrors `RemoteInput.getChoices()`, canned suggestions some apps offer
  alongside free-text typing (Gmail's "Sí"/"No"/"Tal vez" being the common example). Rendered as
  pill buttons above the reply row, always visible (unlike the free-text row, which only opens on
  tapping the reply action) — tapping one sends immediately via the same `action.invoke`, no typing
  or confirm step, matching how tapping one on the phone itself behaves. The choice text is read
  from the clicked button's own `textContent` at send time rather than round-tripped through a
  `data-*` attribute — `escapeHtml()` doesn't escape double-quotes, so app-provided text (not
  user-typed, but still not fully trusted) could otherwise break out of an attribute.
- **The reply box now closes itself on blur.** It used to only ever close on a successful send —
  tapping "Responder", then clicking away without sending, left it open indefinitely. Fixed with a
  delegated `focusout` listener on the list container (bubbles, unlike `blur`) checking
  `e.relatedTarget` against the row itself, so moving focus to the row's own "Enviar" button
  doesn't also trigger a close.

## Notification list ordering, timestamps, and the top-right icon cluster

- **The list is always kept sorted newest-first by each notification's own `postedAt`** —
  `insertSorted()` in `app.js` removes then re-inserts a key at the correct sorted position on
  every `upsertNotification`, rather than the earlier plain-insertion-order approach (`unshift` on
  arrival, later patched to also bump an *update* to the front). Plain insertion order broke in
  two real ways: the initial resync right after (re)connecting sends every currently-active
  notification in whatever order the phone happens to iterate them internally, not by time, so it
  rendered visibly out of order; and a notification arriving late (a flaky connection, a delayed
  sync) with an *older* `postedAt` than what's already showing must not jump to the top just
  because it was the last one received.
- **`postedAt` is deliberately not always `StatusBarNotification.postTime`** — that field is the
  wall-clock moment the OS last saw `notify()` called for that key, which some apps/OEMs bump on
  an update that isn't new real-world content (confirmed live: a notification from the night
  before showed a misleadingly-recent "this morning" time because of this). `bestPostedAt` in
  `NotiLinkNotificationListener.sendPosted` prefers, in order: the last individual message's own
  timestamp for `style == "messaging"` (most granular, set per-message by the chat app itself) →
  `Notification.when` (what the app explicitly asked its own shade entry to show — WhatsApp et al.
  set this correctly for exactly this reason) → `sbn.postTime` as a last resort.
- **The PC's own display of that timestamp** (`formatTime()`) shows a bare `HH:mm` only for
  something posted *today* (compared by calendar day, not a 24h-elapsed cutoff — "yesterday at
  23:50" is a different day even under 24h ago). Anything older shows date *and* time together
  ("Ayer, 23:10", "24 ago, 23:10", or "24 ago 25, 23:10" across a year boundary) — a bare date
  alone was tried first and reverted per feedback that the hour still matters (was it a late-night
  message or a morning one), it's only the bare `HH:mm` *alone* that misleads once the day isn't
  today.
- **Eliminar ("×") moved from the bottom footer to the top-right corner**, rightmost, next to
  contraer — per user feedback that the bottom-right spot wasn't where they expected to find it.
  `topRightIconsHtml` now renders time/silenciar/contraer/eliminar as one fixed cluster (right:
  130/90/50/14px respectively — `.card-top-time`/`.mute-btn`/`.collapse-btn`/`.delete-btn`); the
  bottom footer/floating corner now only ever holds "Abrir en el teléfono" (`.delete-btn-floating`
  no longer exists). `.header-row`/`.header-avatar-row`'s `padding-right` bumped 140→180px to keep
  the header text clear of the now-wider icon cluster.

## Messaging-style attribution: "Tú" vs. the contact, bold sender, mine/theirs color

- **`isMe` (per message) is Android's own documented MessagingStyle convention, not a guess**: a
  message with no `Person` attached at all is the local device owner's own message (that's
  literally how the system's default "You" fallback text works when an app doesn't bother
  attributing its own messages) — `StyleExtractor.isOwnMessage` additionally matches against
  `EXTRA_MESSAGING_PERSON` (the Person the app registered as "the local user") for apps like
  WhatsApp that attribute their own echoed messages explicitly instead of leaving `Person` null,
  so every app is attributed by the same rule rather than trusting whatever literal text ("Tú" /
  "You" / nothing) each one happens to write.
- **Confirmed false positive, now guarded against**: a non-conversational app (a bank's
  transactional alert — confirmed live from Banco Azteca) can use MessagingStyle for a single
  system-generated message with *no* Person set at all, not because it's "mine" but because the
  app never attributes anyone. The blank-Person-means-me convention only holds when the app
  actually follows it — `anyPersonAttached` (true only if at least one *other* message in the same
  notification carries a real Person) gates it, so a notification where nothing is ever attributed
  never has anything wrongly read as "sent by me".
- **The PC only shows a sender prefix when there's actually more than one possible sender to tell
  apart**: always for a group conversation, but for a 1:1 chat only once the user has actually
  replied (i.e. at least one message in the list has `isMe: true`) — until then every message is
  from the one contact already identified by the notification's own header, and repeating their
  name on every line was pure noise. `NotificationItem.ParseMessages`'s `showSender` computes this
  once per notification before building lines. When shown, `Sender` is always the canonical
  display name — "Tú" for `isMe`, otherwise the app-reported name (falling back to the
  conversation's own title for an unattributed message in a 1:1 chat, since there's only one
  other participant it could possibly be).
- **Sender renders as its own bold `<span class="msg-sender">`**, separate from the message text —
  only the "who" is emphasized, not the whole line. `.msg-mine`/`.msg-theirs` on the containing
  `.msg` drives a distinct purple body-text color for the user's own messages (same accent family
  the rest of the app already uses for "this is the user's own doing" — now-playing controls,
  shuffle-on), giving a WhatsApp-bubble-like visual split without needing actual chat bubbles.
- **Twitch is explicitly excluded from messaging classification** (`MESSAGING_EXCLUDED_PACKAGES`,
  keyed by package name) despite Android legitimately classifying its whisper/report notifications
  as `MessagingStyle` — confirmed those aren't a real conversation the user wants grouped with
  WhatsApp/Telegram/etc. `isTreatedAsMessaging(item)` is the one function every messaging-aware
  check (`isMessagingOrEmail`, `summaryLine`, `hasCollapsibleContent`, `cardInnerHtml`'s body
  branch) now goes through, rather than reading `item.isMessaging` directly — a future exclusion
  only needs adding to that one Set. Messaging classification is otherwise entirely dynamic (any
  app whose Android notification uses `MessagingStyle` counts), *not* a hardcoded allow-list —
  only email clients need one (`EMAIL_PACKAGES`), since email virtually never uses that template.

## Opening a notification or the playing app from the PC

"Abrir" (on a card) and "abrir en el dispositivo" (on the now-playing bar) both open the real
thing on the phone — the same tap-through behavior touching the notification would trigger, not
a generic app launch (explicitly required: "no quiero que lances apps, solo quiero que puedas
abrir la notificación"). This was previously investigated and marked confirmed-unfixable (firing
`Notification.contentIntent` directly hits the two-sided BAL wall documented above, and no
third-party app can satisfy the *target* app's side of that check) — it works now via a
completely different mechanism, `NotiLinkAccessibilityService.kt`:

- Simulates the real user gesture instead of firing an Intent: `performGlobalAction(
  GLOBAL_ACTION_NOTIFICATIONS)` opens the shade, then walks every visible window's accessibility
  tree looking for a node whose text contains the notification's own title/text (text tried
  first — far more unique per-notification than the title, which repeats across a whole
  conversation), and clicks the nearest clickable ancestor of that node. Because a real (or
  accessibility-simulated) tap on the notification is what fires the PendingIntent, it's *System
  UI* — already-privileged, foreground-exempt — that ends up starting the activity, not NotiLink.
  This is this app's own reconstruction of the mechanism, not decompiled from anyone else's code;
  the only externally-confirmed fact is that Microsoft's Phone Link ("Link to Windows",
  `com.microsoft.appmanager`) also uses an `AccessibilityService` at all, per Microsoft's own
  support docs.
- Requires a **separate, optional** permission (Settings → Accessibility) well beyond anything
  else this app asks for — a real, meaningfully bigger trust ask than Notification Access, which
  the user explicitly accepted knowing that. `MainActivity`'s permission list has its own row for
  it; `isAccessibilityServiceEnabled()` parses `Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES`
  since there's no `NotificationManagerCompat`-style helper for accessibility services the way
  there is for notification listeners.
- **Android/Samsung can silently revoke this permission on their own** if the app goes unopened
  for a while — a real OS security behavior for apps installed outside Play Store (Accessibility
  is a common malware vector), not a NotiLink bug. There's an "Abrir información de la app"
  button next to the permission list pointing at the closest reliably-reachable settings screen
  (App Info) — no stable public deep link reaches the exact "Pausar actividad si no se usa"
  toggle itself.
- If the phone's screen is off/locked, `wakeScreen()` turns it on first via a real
  `PowerManager.WakeLock` (`SCREEN_BRIGHT_WAKE_LOCK | ACQUIRE_CAUSES_WAKEUP`) — this is screen
  *power* state, unrelated to starting an Activity, so none of the BAL restrictions above apply
  to it. Retries then run much longer (30 attempts × 500ms vs. 6 × 200ms when already unlocked),
  giving room for the user to actually type a PIN — Android's own tap-a-locked-notification
  handling prompts for it and completes the navigation once satisfied, nothing extra needed here.
- **Confirmed the hard way**: the very first version called `Toast.makeText(...).show()` directly
  inside `openNotification()`, which runs on `NotificationConnection`'s socket-read thread, not
  the main thread — `Toast.show()` without a prepared Looper throws immediately, and
  `NotificationConnection`'s own catch-and-log around `onCommand()` silently swallowed it before
  any of the actual open logic ran. "Abrir" looked like it had stopped doing anything at all,
  with zero error surfaced anywhere. Fixed by posting the whole `performOpen()` body onto
  `mainHandler` right away rather than trusting the caller's thread — if a bare
  `AccessibilityService` action stops working session, check this first.
- Always reports back to the PC (`notification.open.result`, success or `reason: "accessibility_
  not_granted" | "not_found"`) so a granted-but-not-found miss never looks identical to the
  permission never having been granted — drives a PC-side toast, same "🖥️ "/"⚠️ " convention as
  screen share's own status toasts.
- The now-playing bar's own "abrir en el dispositivo" button reuses the exact same mechanism via
  a separate `media.open` message — there's no notification `key` for a media session the way
  there is for a mirrored card, so the phone instead looks up the currently-playing app's own
  real "now playing" notification by package name (`MediaSessionWatcher.activePackageName()`).
  That notification still exists and is still in `activeNotifications` even though it's
  deliberately excluded from normal card mirroring (`EXTRA_MEDIA_SESSION` filtering).
- Matching is a best-effort text search, not a guaranteed-unique lookup — two simultaneous
  notifications with identical title *and* body from the same app could match the wrong one.

## Incoming call detection & the call bar

Originally investigated and marked not-attempted (a self-managed Telecom `ConnectionService`'s
incoming-call UI has no `StatusBarNotification` at all, so `NotificationListenerService` has zero
visibility into it) — implemented since via a different signal, confirmed against real
WhatsApp/SIM call logs:

- Android's own Telecom framework registers a session under package `com.android.server.telecom`
  in `MediaSessionManager.getActiveSessions()` for as long as *any* call — native SIM or a VoIP
  app's self-managed `ConnectionService` (WhatsApp, Telegram, etc.) — is ringing or in progress.
  `MediaSessionWatcher.checkCallActive()` watches for this package appearing/disappearing in the
  existing candidate list it already builds for the now-playing bar, and fires `call.state`
  (`{"state": "active"}` / `{"state": "ended"}`) on each edge. No extra permission needed — rides
  on the same notification-listener-granted media session access as the now-playing bar itself.
- **This is a genuinely better signal than the `READ_PHONE_STATE`-based `TelephonyManager`
  approach originally implemented and then removed**: confirmed from a real WhatsApp call log
  that WhatsApp posts *no* notification at all while ringing (only a `missed_call`-category one
  afterward), so `TelephonyManager` — which only reflects the cellular radio's own state — never
  saw it either. The Telecom-session signal caught it, for both native and VoIP calls, with one
  mechanism and no extra permission.
- **Deliberately minimal and honest about its limits**: exposes no title, no direction (incoming
  vs. outgoing — Telecom's session for a call always reports `null` for both), no ringing-vs-
  answered distinction. Can't tell an incoming call from one the phone's own user just placed.
  The PC's call bar reflects this: "Llamada en curso (nombre)", never "entrante".
- **`InCallService` investigated (real research, not assumption) and deliberately rejected** as
  the way to get caller identity for this bare-Telecom-signal case (a VoIP call that never posts a
  notification while ringing — confirmed for WhatsApp). Per Android's own docs
  (developer.android.com/develop/connectivity/telecom/dialer-app), receiving `InCallService`
  callbacks with real call details requires one of: (1) a self-managed `ConnectionService` — for
  handling an app's *own* calls, doesn't apply to observing another app's; (2) `MANAGE_ONGOING_
  CALLS` + `CompanionDeviceManager` association as a *physical wearable* companion — designed
  around real BLE hardware pairing, not a WiFi-paired custom-protocol app like this one, and would
  carry the same kind of Play Store scrutiny already flagged for AccessibilityService; or (3)
  becoming the phone's actual **default dialer app** — full dial-pad UI, full in-call UI, ringtone
  responsibility, and the OS force-removes the role if requirements ever lapse. None of these are
  proportionate for a notification-mirroring utility. Not implemented; the existing coverage
  (every native SIM call, and any VoIP app that *does* post a notification while ringing) already
  covers the large majority of real calls.
- Calls that *do* post a real notification while ringing (native SIM calls via the phone's own
  dialer, typically) get the richer treatment for free through the completely separate, already-
  existing `notification.posted` pipeline — full caller photo/name and the notification's own
  real `Contestar`/`Rechazar`/`Colgar` actions, no special-casing needed since `style == "call"`
  notifications were already mirrored normally, just never highlighted differently ( see the
  `JsNotificationMapper` bug below for why the highlight silently never applied until fixed ).
- **PC-side call bar** (`#call-bar`, between the header/now-playing-bar and the notification
  list) unifies both signals: if a `style: "call"` notification exists for the active device, it
  renders richly (photo/name, a live `mm:ss`/`hh:mm:ss` duration computed from that notification's
  own `postedAt`, and its real actions rendered as colored circular buttons — green for
  contestar/answer-ish titles, red hang-up-icon-rotated-135° for colgar/rechazar-ish ones,
  matched by regex against the action's visible title, same best-effort approach as favorite/
  shuffle detection). Otherwise, if only the bare Telecom signal is active, it falls back to a
  plain "Llamada en curso" text row. The call-style notification is deliberately hidden from the
  normal card list once it's shown here (`renderAll` filters `style === 'call'` out) — it lived
  in both places at first, which read as a confusing duplicate.
- **Real bug, not a design choice**: `JsNotificationMapper.ToJson`'s `JsNotification` DTO never
  included a `Style` field at all — a pre-existing gap (not introduced alongside the call bar,
  but only surfaced by it), meaning `item.style` was `undefined` in every card's JS the whole
  time, `cardClassFor`'s `.card-call` highlight border had *never* actually applied despite the
  code looking correct, and the call bar's own `style === 'call'` detection couldn't work either
  until `Style` was added to the DTO. If a `NotificationItem` field seems to have no visible
  effect in the UI, check whether it's actually present in `JsNotificationMapper`'s DTO before
  assuming the bug is elsewhere.

- **The call bar's timer no longer runs while a call is still ringing, unanswered.** It used to
  count from the call notification's own `postedAt` unconditionally, which — since that's when
  the call started *ringing*, not when it was answered — made an incoming call look already
  connected the moment it appeared. `isCallRinging()` detects ringing via the same signal already
  used to tell the answer/hang-up buttons apart: Android's `CallStyle` only shows an
  answer-shaped action while ringing (`forIncomingCall`), swapping to just hang-up once answered
  (`forOngoingCall`) — no dedicated "is this ringing" field exists on the wire. While ringing, the
  bar shows a static "Llamada entrante" label and no ticking number; `callAnsweredAt` (a
  client-observed `Date.now()`, captured the moment the answer action is first seen to disappear
  for that call's key) is what the timer actually counts from once answered — deliberately not
  `postedAt`, which would still include however long the call rang before being picked up.

See `docs/PROTOCOL.md`'s `notification.open`/`notification.open.result`/`media.open` and
`call.state` for the exact wire messages.

## Real-time audio waveform (screen/audio sharing) vs. the decorative media-player one

Two visually similar equalizer-style indicators exist, and they are **not** the same kind of
thing — worth being explicit about which is which before changing either:

- **The now-playing bar's own equalizer** (next to the song title) is purely decorative, a canned
  CSS `@keyframes` bounce shown whenever `media.isPlaying` — NotiLink has no access to Spotify's
  actual audio samples, only playback control, so there's nothing real to visualize there.
- **The screen/audio-share header icon's waveform is genuinely real-time**, because that audio
  case is different: `scrcpy` forwards the phone's audio and plays it through a normal Windows
  audio session under its own process, exactly like any other app — something this PC can
  actually measure. `Services/AudioLevelMonitor.cs` wraps WASAPI's per-process peak metering (via
  the NAudio package — .NET has no built-in Core Audio API surface) to read that session's real
  `AudioMeterInformation.MasterPeakValue`, ~10×/second, for whichever scrcpy process
  (`_videoProcess` when started with `audio: true`, or `_audioProcess` for the audio-only mode —
  tracked via an explicit "video"/"audio" ownership string so stopping one can never blindly tear
  down a watch the other one actually owns) is currently forwarding sound.
- **Two real problems had to be fixed before this was actually visible/useful**, both confirmed
  live against the real feature, not guessed:
  1. WASAPI's peak value is linear amplitude; loudness is perceived roughly logarithmically. A
     fixed power-curve boost (`Math.pow(level, 0.4)`) still wasn't enough — at the phone's normal/
     medium volume the raw peak barely lifted off 0.05–0.15, so nothing visibly moved; only
     near-max phone volume pushed it close to 1.0.
  2. Fixed with a rolling auto-gain ceiling instead of any fixed curve: `window.setAudioLevel`
     tracks a slowly-decaying "how loud has it recently gotten" ceiling
     (`audioLevelCeiling`, decay `0.985` per ~100ms tick, floor `0.02`) and scales the current
     level as a fraction of *that* — so the wave always uses the full visual range regardless of
     the phone's absolute volume, and can still adapt if the user changes the phone's volume
     mid-session.
- `AudioLevelMonitor.SessionNotFound` fires (→ a PC toast) if no matching session is ever found
  within ~8s of retrying — concrete signal for "I don't see anything" reports, since a silent
  give-up would otherwise look identical to the effect just being too subtle to notice.

## PC ↔ PC link ("PCs vinculadas")

Two NotiLink PCs can link to each other: chat, clipboard text, files of any size, a nudge. Header
button (two monitors) → `#pcs-overlay` in `app.js`/`index.html`; C# side is
`NotiLink.Core/Networking/PeerLinkService.cs` (+ `Storage/PeerStore.cs`, DPAPI, and
`Storage/PeerChatStore.cs`, plain JSON history per PC under `%LocalAppData%\NotiLink\pc-chat\`).
Spec: `docs/PROTOCOL.md`'s "PC ↔ PC".

- **Same TCP port as the phones (45212), not a new one.** `TcpServer.PeerHandoff` hands any
  connection whose first frame is `peer.hello` to `PeerLinkService` — so whatever firewall rule or
  Tailscale setup already lets a phone in works for another PC with nothing extra to configure.
- **Symmetric, both sides redial.** Duplicate links are resolved by a rule both sides compute the
  same way (`RegisterConnection`); "current link" checks mirror `TcpServer`'s stale-socket handling.
- **Pairing code is shown on the PC being added and typed on the one that clicked "Añadir PC"** —
  same trust model as the phone (6 digits once, then a stored token), no TLS (same accepted trade-off).
- **Files never pass through the page as base64** (unlike phone `file.send`): the page hands the
  real `File` objects over with `chrome.webview.postMessageWithAdditionalObjects`, C# gets their
  paths (`CoreWebView2File.Path`) and streams 512 KB chunks from disk. Received files land in
  Downloads via a `*.notilink-part` temp name.
- **Each linked PC also gets a tab in the device switcher**, after the phones (`peer-segment`,
  `activePeerTab` in `app.js`; `activeDeviceId` keeps the last phone). Selecting it sets
  `body.peer-tab` (hides every phone-only control via `theme.css`), shows `#peer-feed-bar` (write /
  files / clipboard / nudge / open chat) and renders that PC's exchanged items as cards in `#list`
  (`renderPeerFeed`) — never that PC's own notifications. "×" only sets `Dismissed` on the history
  item (`peerDismiss`), so the chat keeps it. Peer cards' clicks are handled in a *capture* listener
  on `#list` that stops propagation, so the phone cards' delegates never see them.
- **Screen sharing between PCs is NotiLink's own, not scrcpy** (scrcpy only mirrors Android).
  `Services/ScreenCaptureService.cs` captures the primary monitor with **DXGI Desktop Duplication**
  (Vortice.Direct3D11; device created on the adapter that owns that output — confirmed working on
  this hybrid-GPU laptop, ~15 ms GPU→bitmap copy) with GDI `CopyFromScreen` as the fallback
  (measured ~28 ms per 1920×1200 grab — that, not JPEG encoding at ~5 ms, was what capped the first
  version near 15–30 fps). It encodes only when the screen or the cursor (drawn in by hand via
  `GetCursorInfo`/`DrawIconEx`; neither API includes it) changed, JPEG q65, ≤ 1920 px wide, ≤ 60
  fps (30 on GDI), and only while `PeerLinkService.ScreenViewers()` is non-empty; frames ride the
  same peer link, acked one by one (≤ 2 in flight per viewer, each viewer always gets the newest
  frame). The viewer is a native WPF window (`PeerScreenWindow.cs`, newest frame only, fps counter,
  F11/double click or Ctrl+Alt+Enter = full screen) — not the WebView page, so frames never go
  through base64/JS. Viewing someone's screen needs their OK; "Permitir siempre" is
  `PairedPeer.AllowScreen` (revocable from the PC's ⋯ menu). A red header pill (`#peer-screen-chip`)
  shows whenever this PC is being watched or controlled, on any tab; clicking it stops everything.
  Secure-desktop moments (UAC, lock screen) can't be captured — frames just pause.
- **Remote control rides on a screen share** (`peer.control.*`, `peer.input`): its own permission
  (`PairedPeer.AllowControl`, same once/always prompt), only while that viewer is watching, and it
  ends with the share. The viewer window forwards mouse (moves coalesced to one per ~8 ms) and every
  key — Alt+F4 included — except Ctrl+Alt+Enter (full screen) and Ctrl+Alt+Q (let go); the sharer
  injects with `SendInput` (`Services/InputInjector.cs`, absolute coordinates over the virtual
  desktop, extended-key flag for arrows/Ins/Del/etc.) straight from the socket thread, and
  `ReleaseAll()`s any held key/button when control ends. Windows' own limits apply: no input into
  elevated (admin) windows (UIPI), none on the secure desktop, and keys the viewer's own Windows
  keeps (Win key, Alt+Tab, Ctrl+Alt+Supr) never reach the window to be forwarded.
- **`ShowFromTray()` must never force `WindowState.Normal`** — it used to, which un-maximized (and
  so moved) a maximized window on every nudge/tray click. It now only restores a *minimized*
  window to `_lastShownState` (tracked in `StateChanged`) and flashes the taskbar if Windows
  refused to bring it to the front.
- `NotiLink.Tests/PeerLinkTests.cs` runs two real services against each other over loopback
  (pairing incl. a wrong code, message, clipboard, multi-chunk file, nudge, unpair, and an
  automatic token-based reconnect after a "restart"). Like everything else here it needs the dev
  signing certificate on machines with Smart App Control, or the test DLL is blocked from loading.

## Project history in brief

Started as a Phone Link replacement after Phone Link's pairing broke persistently on the user's
machine (root cause never fully identified — plausibly a Cross Device Service issue). Built up
incrementally: raw-socket smoke test → real notification mirroring with style-aware extraction
(plain/bigText/bigPicture/messaging/inbox) → images/icons/avatars → delete + real action
buttons → live battery → per-app filter (synced both directions) → QR + UDP pairing → multi-
device support → Dual Messenger support → Wi-Fi/cellular status → wallpaper-as-background →
system tray + close-to-minimize + auto-start packaging → "abrir en el dispositivo" tried via a
direct `contentIntent` and abandoned (real BAL platform limit for that specific approach) →
scrcpy vendored and wired up end to end (video with full quality controls, a separate audio-only
mode, phone-initiated sharing with its own status notification) → a persistent Spotify-style
now-playing bar (transport controls, favorite/shuffle via custom actions, cached+retried album
art) → a per-device custom wallpaper picker, collapse/expand and delete card animations, and a
light-theme pass to fix a washed-out header/device-switcher without breaking wallpaper visibility
→ a portable migration package for moving to a different PC without the dev toolchain → **a
second, successful attempt at "abrir en el dispositivo"** via `NotiLinkAccessibilityService`
(simulated tap instead of a direct Intent, sidestepping the earlier BAL wall entirely), extended
to the now-playing bar too, plus screen-wake-before-open and PC-side success/failure toasts →
**incoming call detection** via a different, better signal than originally planned
(`com.android.server.telecom`'s own Telecom session, not `TelephonyManager`) after the original
`READ_PHONE_STATE` approach turned out to miss VoIP calls entirely, plus a persistent call bar
unifying that bare signal with richer real-notification-backed calls → now-playing bar
additions: a live seek/scrub bar with real `seekTo()`, Smart Shuffle detection/icon, a real-time
WASAPI-based audio waveform for screen/audio sharing (distinct from the media bar's own
decorative one) → notification card extras: link-copy button, an image lightbox with wheel-zoom,
and open-file/open-path buttons for phone-to-PC file sharing → a PC-side volume control (real
`AudioManager` read/write, phone hardware-button changes reflected too) and a text-box-based
"send to PC" replacing the old test-notification debug button, plus custom vector notification
action icons replacing emoji → `invokeAction` went from zero DiagLog visibility to full
instrumentation, which is what actually let three real notification-action failures get root-
caused from a live log instead of guessed at (a canceled `PendingIntent`, an action that silently
succeeded, and "Abrir en el teléfono" failing on bidi/invisible-character mismatches plus a
MessagingStyle summary-vs-rendered-text mismatch) → **a PC-local media control toggle** (flip
animation, real Windows SMTC integration, shuffle support, honest about favorite/smart-shuffle
being genuinely unavailable at the OS level) → messaging attribution overhaul (`isMe` on Android's
own documented convention, a confirmed false-positive fix for non-conversational apps, sender only
shown once there's actually more than one possible sender, bold sender name + mine/theirs color,
Twitch excluded from messaging classification) → notification list correctness passes: always
sorted by real timestamp rather than arrival order, `postedAt` preferring the actual message/
`Notification.when` time over an OS field that can get bumped by an unrelated update, date shown
alongside time once a notification isn't from today, eliminar moved to the top-right corner → the
call bar no longer shows a running timer while a call is still ringing, unanswered → **reconnect
robustness**: a real dead-connection watchdog on the PC (the timeout `docs/PROTOCOL.md` had always
described but that was never actually implemented), matched by a bounded socket timeout on Android
that used to wait indefinitely too, both confirmed necessary after a real report of a phone's own
audio staying stuck captured until reboot when it silently left WiFi mid-share, plus a
transient-failure retry for the DPAPI-encrypted pairing store after a repeatable "asks to re-pair
every PC reboot" report → adding a phone-side read timeout without also giving the PC a periodic
send of its own immediately regressed into a *different* "se conecta y desconecta a cada rato",
fixed by having the PC send its own heartbeat too (and the watchdog loosened from 45s to 3 minutes
once real logs showed the tight value was landing on genuine OEM background-scheduling jitter, not
just dead connections) → `notification.sync`: the phone now sends its complete current key set on
every reconnect so the PC can clear a card whose removal happened while genuinely offline (Quick
Share-style network switches were losing that event entirely, confirmed) → a real bug in the
"Desconectar" recovery path: scanning a fresh QR/using "Buscar" only ever saved the new PC IP,
never actually attempted reconnecting, so a phone stuck manually-disconnected had no obvious way
back short of deleting and re-pairing → the PC side finally got its own persistent diagnostic log
(`PcDiagLog`, mirroring the phone's DiagLog) after realizing `TcpServer.Log` had never actually
been wired to anything, specifically to stop guessing at *why* the pairing code still reappears
sometimes → three more real notification fields wired up: a progress bar (`Notification.
EXTRA_PROGRESS`, after a confirmed key-presence-vs-value bug briefly showed an empty bar on
everything), the app's own requested accent color (`Notification.color`), and `RemoteInput
.getChoices()` quick-reply pills → `InCallService` researched seriously (not assumed) and
deliberately rejected as a way to get caller identity for VoIP calls with no notification — every
real path either doesn't apply to observing another app's calls or costs far more than this
feature is worth → a first pass at wider distribution: a written privacy policy and a fully
pre-drafted Google Play submission checklist, ready for GitHub Pages/Play Console whenever that's
actually acted on. Most UI iteration happened directly in `wwwroot/` against user feedback on the
actual running app (dark-blue-tinted theme over flat black, glass/blur per-card backgrounds,
device switcher as a segmented control, a device options menu instead of scattered icon buttons,
rounded card corners as a user-adjustable setting, delete/mute relocated more than once as real
usage surfaced a better spot). A recurring, deliberate pattern throughout: root-cause every bug
from a real on-device DiagLog capture (now a PC-side `pc.log` too) or from actually reading the
relevant API docs/source rather than assuming, before writing a fix, never guess — this is how the
album art, reconnect, media-flood, Wi-Fi-icon, device-rename, seek-fingerprint,
`JsNotificationMapper` style-field, `postedAt`, Banco Azteca false-positive, dead-connection-
watchdog, and progress-bar-value-vs-key-presence bugs all actually got found.
