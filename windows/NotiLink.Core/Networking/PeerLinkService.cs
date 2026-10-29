using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using NotiLink.Core.Protocol;
using NotiLink.Core.Storage;

namespace NotiLink.Core.Networking;

/// <param name="AllowScreen">It may view this PC's screen without asking.</param>
/// <param name="SharingScreen">This PC is sending its screen to it right now.</param>
/// <param name="ViewingScreen">This PC is watching its screen right now.</param>
/// <param name="AllowControl">It may control this PC (while viewing it) without asking.</param>
/// <param name="ControlledBy">It is controlling this PC right now.</param>
/// <param name="Controlling">This PC is controlling it right now.</param>
public sealed record PeerInfo(string PcId, string Name, bool Connected, string? Address, long LastSeen, bool NeedsRepair,
    bool AllowScreen = false, bool SharingScreen = false, bool ViewingScreen = false,
    bool AllowControl = false, bool ControlledBy = false, bool Controlling = false);

/// <summary>One remote-control event from the viewer. X/Y are 0..1 across the shared monitor.
/// Type: "move" | "down" | "up" (Button: "left" | "right" | "middle" | "x1" | "x2") | "wheel"
/// (Delta, 120 per notch) | "key" (Vk = Windows virtual-key code, Down).</summary>
public sealed record PeerInput(string Type, double X = 0, double Y = 0, string? Button = null, int Delta = 0, int Vk = 0, bool Down = false);
public sealed record DiscoveredPc(string PcId, string Name, string Ip, bool AlreadyLinked);

/// <summary>
/// PC ↔ PC link (see docs/PROTOCOL.md's "PC ↔ PC"): chat messages, clipboard text, files of any
/// size and a "llamar la atención" nudge between NotiLink PCs. Rides on the same TCP port as the
/// phones (TcpServer hands any connection whose first frame is peer.hello over to AcceptIncoming),
/// so whatever firewall/Tailscale setup already lets a phone in works for another PC too.
///
/// Symmetric: either PC can dial the other. Both dial their known peers while disconnected, so
/// two connections can briefly exist at once — RegisterConnection keeps exactly one with a rule
/// both sides compute identically (same initiator → the newer wins; otherwise the one dialed by
/// the lower pcId wins), so they always agree on which socket is "the" link.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PeerLinkService : IDisposable
{
    private const int FileChunkSize = 512 * 1024;
    private const int MaxFrameLength = 8 * 1024 * 1024;
    private const int MaxPairAttempts = 3;
    private static readonly TimeSpan SilentTimeout = TimeSpan.FromSeconds(60);

    private readonly PeerStore _store;
    private readonly PeerChatStore _chat;
    private readonly string _downloadsDir;
    private readonly int _port;
    private readonly Func<IReadOnlyList<string>> _myAddresses;
    private readonly Dictionary<string, PeerConnection> _connections = new(); // pcId -> current link
    private readonly HashSet<string> _dialing = new();
    private readonly HashSet<string> _needsRepair = new();   // the other PC no longer accepts our token
    private readonly Dictionary<string, OutgoingTransfer> _outgoing = new();
    // Screen sharing, both under lock(_screenOut): who we send our screen to (→ frames sent but not
    // acked yet — the flow control), and whose screen we're watching.
    private const int MaxScreenFramesInFlight = 2;
    private readonly Dictionary<string, int> _screenOut = new();
    private readonly HashSet<string> _screenIn = new();
    // Remote control, also under lock(_screenOut): peers allowed to drive this PC right now (only
    // ever ones we're sharing our screen with), and peers this PC is driving.
    private readonly HashSet<string> _controlOut = new();
    private readonly HashSet<string> _controlIn = new();
    private readonly CancellationTokenSource _cts = new();
    private Timer? _timer;
    private int _tick;

    // Target side of a pairing in progress (the PC showing the code). One at a time.
    private PeerConnection? _pairingIncoming;
    // Initiator side: the connection waiting for the user to type the other PC's code.
    private PeerConnection? _pairingOutgoing;

    public string MyPcId { get; }
    public static string MyName => Environment.MachineName;

    public event Action? PeersChanged;
    public event Action<string, PeerChatItem>? ChatItemChanged;   // pcId, item (insert or update)
    public event Action<string, string>? PairCodeShown;           // requesting PC's name, code to show here
    public event Action? PairCodeDismissed;
    public event Action<string>? PairCodeNeeded;                  // name of the PC we're adding — type its code
    public event Action<bool, string>? PairResult;                // success, message
    public event Action<string, string>? ClipboardReceived;       // pcId, text
    public event Action<string, string>? NudgeReceived;           // pcId, peer name
    public event Action<string, string>? ScreenRequested;         // pcId, name — wants to see this screen, no standing permission: ask
    public event Action<string, bool>? ScreenSharingChanged;      // pcId, now sending our screen to it
    public event Action<string, string>? ScreenViewStarted;       // pcId, name — its screen is coming: open a viewer
    public event Action<string, byte[]>? ScreenFrameReceived;     // pcId, JPEG
    public event Action<string, string>? ScreenViewEnded;         // pcId, reason: "stopped" | "denied" | "disconnected" | "closed"
    public event Action<string, string>? ControlRequested;        // pcId, name — wants to control this PC, no standing permission: ask
    public event Action<string, bool>? ControlledByChanged;       // pcId, it may now drive this PC (sharer side)
    public event Action<string, bool>? ControllingChanged;        // pcId, this PC now drives it (viewer side)
    public event Action<string>? ControlDenied;                   // pcId — our control request was rejected
    public event Action<string, PeerInput>? InputReceived;        // pcId, event to inject (only while ControlledBy)
    public event Action<string>? Log;

    /// <param name="port">The port other PCs listen on (always TcpServer.DefaultPort in the app).</param>
    /// <param name="dataDir">Where links/history live (default %LocalAppData%\NotiLink) — tests use a temp dir.</param>
    /// <param name="downloadsDir">Where received files go (default the user's Downloads).</param>
    public PeerLinkService(Func<IReadOnlyList<string>> myAddresses, int port = TcpServer.DefaultPort,
        string? dataDir = null, string? downloadsDir = null)
    {
        _myAddresses = myAddresses;
        _port = port;
        _store = new PeerStore(dataDir);
        _chat = new PeerChatStore(dataDir == null ? null : Path.Combine(dataDir, "pc-chat"));
        _downloadsDir = downloadsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        MyPcId = _store.GetOrCreatePcId();
    }

    public void Start()
    {
        _timer = new Timer(_ => MaintenanceTick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15));
    }

    // ---------------------------------------------------------------- public API (UI-facing)

    public IReadOnlyList<PeerInfo> ListPeers()
    {
        var peers = _store.List();
        lock (_connections)
        {
            return peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p =>
                {
                    var connected = _connections.TryGetValue(p.PcId, out var c);
                    bool sharing, viewing, controlledBy, controlling;
                    lock (_screenOut)
                    {
                        sharing = _screenOut.ContainsKey(p.PcId);
                        viewing = _screenIn.Contains(p.PcId);
                        controlledBy = _controlOut.Contains(p.PcId);
                        controlling = _controlIn.Contains(p.PcId);
                    }
                    return new PeerInfo(p.PcId, p.Name, connected, connected ? c!.RemoteIp : p.Addresses.FirstOrDefault(),
                        p.LastSeen, _needsRepair.Contains(p.PcId), p.AllowScreen, sharing, viewing,
                        p.AllowControl, controlledBy, controlling);
                }).ToList();
        }
    }

    public IReadOnlyList<PeerChatItem> History(string pcId) => _chat.List(pcId);

    public void ClearHistory(string pcId) => _chat.Clear(pcId);

    /// <summary>Hides one activity card ([id]) or all of them ([id] null) from that PC's tab,
    /// keeping the conversation itself.</summary>
    public void DismissChatItems(string pcId, string? id) => _chat.Dismiss(pcId, id);

    public PeerChatItem? FindChatItem(string pcId, string id) => _chat.Find(pcId, id);

    public void Rename(string pcId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.Update(pcId, p => p with { Name = name.Trim() });
        PeersChanged?.Invoke();
    }

    /// <summary>Forgets the other PC here and tells it to forget this one too (if reachable).</summary>
    public async Task UnpairAsync(string pcId)
    {
        EndScreenOut(pcId, notifyPeer: false);
        EndScreenIn(pcId, "closed", notifyPeer: false);
        var conn = Current(pcId);
        if (conn != null)
        {
            await conn.SendAsync("peer.unpair", new { }).ConfigureAwait(false);
            conn.Close();
        }
        _store.Remove(pcId);
        lock (_connections) { _connections.Remove(pcId); _needsRepair.Remove(pcId); }
        PeersChanged?.Invoke();
    }

    /// <summary>"Añadir PC": dials [ip] and starts the 6-digit code flow (or reconnects silently
    /// if that PC still trusts us). The outcome arrives via PairCodeNeeded / PairResult.</summary>
    public async Task BeginPairAsync(string ip)
    {
        CancelPairing();
        if (!IPAddress.TryParse(ip.Trim(), out var address))
        {
            PairResult?.Invoke(false, "Esa dirección IP no es válida.");
            return;
        }
        var conn = await DialAsync(address.ToString(), userInitiated: true).ConfigureAwait(false);
        if (conn == null) PairResult?.Invoke(false, $"No se pudo conectar con {ip}. ¿Está NotiLink abierto en esa PC y en la misma red (o en Tailscale)?");
    }

    public Task SubmitPairCodeAsync(string code)
    {
        var conn = _pairingOutgoing;
        if (conn == null) { PairResult?.Invoke(false, "La solicitud ya no está activa. Vuelve a intentarlo."); return Task.CompletedTask; }
        return conn.SendAsync("peer.pair.code", new { code = code.Trim() });
    }

    public void CancelPairing()
    {
        var outgoing = Interlocked.Exchange(ref _pairingOutgoing, null);
        outgoing?.Close();
    }

    /// <summary>Target side declining a pairing request it never asked for.</summary>
    public void RejectIncomingPairing()
    {
        var incoming = Interlocked.Exchange(ref _pairingIncoming, null);
        if (incoming == null) return;
        _ = incoming.SendAsync("peer.pair.fail", new { reason = "rejected", attemptsLeft = 0 }).ContinueWith(_ => incoming.Close());
        PairCodeDismissed?.Invoke();
    }

    /// <summary>Text or clipboard text ([kind] "text" | "clipboard"). False if not connected.</summary>
    public async Task<bool> SendTextAsync(string pcId, string text, string kind = "text")
    {
        var conn = Current(pcId);
        if (conn == null || string.IsNullOrEmpty(text)) return false;
        var item = new PeerChatItem { Id = Guid.NewGuid().ToString("N"), Outgoing = true, Kind = kind, Text = text, At = Now() };
        await conn.SendAsync("peer.message", new { id = item.Id, kind, text, at = item.At }).ConfigureAwait(false);
        _chat.Upsert(pcId, item);
        ChatItemChanged?.Invoke(pcId, item);
        return true;
    }

    public async Task<bool> NudgeAsync(string pcId)
    {
        var conn = Current(pcId);
        if (conn == null) return false;
        await conn.SendAsync("peer.nudge", new { }).ConfigureAwait(false);
        var item = new PeerChatItem { Id = Guid.NewGuid().ToString("N"), Outgoing = true, Kind = "nudge", At = Now() };
        _chat.Upsert(pcId, item);
        ChatItemChanged?.Invoke(pcId, item);
        return true;
    }

    // ---------------------------------------------------------------- screen sharing
    // The sharing PC captures (the app's ScreenCaptureService) and sends JPEG frames over this same
    // link; the viewer acks each one and the sender keeps at most MaxScreenFramesInFlight unacked,
    // so a slow network just lowers the frame rate instead of piling up a backlog. Viewing someone
    // else's screen needs their OK — once ("Permitir esta vez") or for good (AllowScreen).

    public void SetAllowScreen(string pcId, bool allow)
    {
        _store.Update(pcId, p => p with { AllowScreen = allow });
        PeersChanged?.Invoke();
    }

    /// <summary>Viewer: ask that PC for its screen. It starts right away if that PC already allows
    /// us, otherwise its user is asked; the answer arrives as ScreenViewStarted / ScreenViewEnded("denied").</summary>
    public async Task<bool> RequestScreenAsync(string pcId)
    {
        var conn = Current(pcId);
        if (conn == null) return false;
        lock (_screenOut) { if (_screenIn.Contains(pcId)) return true; }
        return await conn.SendAsync("peer.screen.request", new { }).ConfigureAwait(false);
    }

    /// <summary>Sharer: start sending this PC's screen to that PC (it opens a viewer by itself).</summary>
    public async Task<bool> ShareScreenAsync(string pcId)
    {
        var conn = Current(pcId);
        if (conn == null) return false;
        lock (_screenOut)
        {
            if (_screenOut.ContainsKey(pcId)) return true;
            _screenOut[pcId] = 0;
        }
        if (!await conn.SendAsync("peer.screen.start", new { }).ConfigureAwait(false))
        {
            lock (_screenOut) _screenOut.Remove(pcId);
            return false;
        }
        Log?.Invoke($"peer: sharing screen with {pcId}");
        ScreenSharingChanged?.Invoke(pcId, true);
        PeersChanged?.Invoke();
        return true;
    }

    /// <summary>Sharer: the user's answer to a ScreenRequested prompt.</summary>
    public void AnswerScreenRequest(string pcId, bool allow, bool always)
    {
        if (allow && always) SetAllowScreen(pcId, true);
        if (allow) _ = ShareScreenAsync(pcId);
        else _ = Current(pcId)?.SendAsync("peer.screen.denied", new { });
    }

    public void StopSharingScreen(string pcId) => EndScreenOut(pcId, notifyPeer: true);

    public void StopViewingScreen(string pcId) => EndScreenIn(pcId, "closed", notifyPeer: true);

    /// <summary>PCs this one is currently sending its screen to.</summary>
    public IReadOnlyList<string> ScreenViewers()
    {
        lock (_screenOut) return _screenOut.Keys.ToList();
    }

    /// <summary>False while that viewer still has MaxScreenFramesInFlight frames unacknowledged.</summary>
    public bool WantsScreenFrame(string pcId)
    {
        lock (_screenOut) return _screenOut.TryGetValue(pcId, out var inFlight) && inFlight < MaxScreenFramesInFlight;
    }

    public async Task SendScreenFrameAsync(string pcId, byte[] jpeg, int width, int height)
    {
        var conn = Current(pcId);
        if (conn == null) return;
        lock (_screenOut)
        {
            if (!_screenOut.TryGetValue(pcId, out var inFlight)) return;
            _screenOut[pcId] = inFlight + 1;
        }
        await conn.SendWithBinaryAsync("peer.screen.frame", new { width, height }, jpeg).ConfigureAwait(false);
    }

    // ---- remote control (only on top of an active screen share: the viewer drives the sharer)

    public void SetAllowControl(string pcId, bool allow)
    {
        _store.Update(pcId, p => p with { AllowControl = allow });
        PeersChanged?.Invoke();
    }

    /// <summary>Viewer: ask to drive the PC whose screen we're watching. Granted right away if it
    /// allows us for good, otherwise its user is asked (ControllingChanged / ControlDenied).</summary>
    public async Task<bool> RequestControlAsync(string pcId)
    {
        var conn = Current(pcId);
        if (conn == null) return false;
        lock (_screenOut)
        {
            if (!_screenIn.Contains(pcId)) return false;
            if (_controlIn.Contains(pcId)) return true;
        }
        return await conn.SendAsync("peer.control.request", new { }).ConfigureAwait(false);
    }

    /// <summary>Sharer: the user's answer to a ControlRequested prompt.</summary>
    public void AnswerControlRequest(string pcId, bool allow, bool always)
    {
        if (allow && always) SetAllowControl(pcId, true);
        if (allow) GrantControl(pcId);
        else _ = Current(pcId)?.SendAsync("peer.control.denied", new { });
    }

    /// <summary>Ends remote control between this PC and [pcId], whichever side is driving.</summary>
    public void StopControl(string pcId)
    {
        EndControlOut(pcId, notifyPeer: true);
        EndControlIn(pcId, notifyPeer: true);
    }

    /// <summary>Viewer: forward one input event. Dropped unless we hold control of that PC.</summary>
    public void SendInput(string pcId, PeerInput input)
    {
        lock (_screenOut) { if (!_controlIn.Contains(pcId)) return; }
        _ = Current(pcId)?.SendAsync("peer.input", new
        {
            t = input.Type, x = input.X, y = input.Y, b = input.Button, d = input.Delta, vk = input.Vk, down = input.Down,
        });
    }

    private void GrantControl(string pcId)
    {
        lock (_screenOut)
        {
            if (!_screenOut.ContainsKey(pcId)) return; // control only rides on a live screen share
            if (!_controlOut.Add(pcId)) return;
        }
        _ = Current(pcId)?.SendAsync("peer.control.granted", new { });
        Log?.Invoke($"peer: {pcId} may control this PC");
        ControlledByChanged?.Invoke(pcId, true);
        PeersChanged?.Invoke();
    }

    private void EndControlOut(string pcId, bool notifyPeer)
    {
        bool was;
        lock (_screenOut) was = _controlOut.Remove(pcId);
        if (!was) return;
        if (notifyPeer) _ = Current(pcId)?.SendAsync("peer.control.stop", new { role = "sharer" });
        Log?.Invoke($"peer: {pcId} no longer controls this PC");
        ControlledByChanged?.Invoke(pcId, false);
        PeersChanged?.Invoke();
    }

    private void EndControlIn(string pcId, bool notifyPeer)
    {
        bool was;
        lock (_screenOut) was = _controlIn.Remove(pcId);
        if (!was) return;
        if (notifyPeer) _ = Current(pcId)?.SendAsync("peer.control.stop", new { role = "viewer" });
        ControllingChanged?.Invoke(pcId, false);
        PeersChanged?.Invoke();
    }

    private static PeerInput? ParseInput(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || Str(p, "t") is not { } type) return null;
        double D(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetDouble(), 0, 1) : 0;
        int I(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        var down = p.TryGetProperty("down", out var dn) && dn.ValueKind == JsonValueKind.True;
        return new PeerInput(type, D("x"), D("y"), Str(p, "b"), I("d"), I("vk"), down);
    }

    private void EndScreenOut(string pcId, bool notifyPeer)
    {
        EndControlOut(pcId, notifyPeer: false);
        bool was;
        lock (_screenOut) was = _screenOut.Remove(pcId);
        if (!was) return;
        if (notifyPeer) _ = Current(pcId)?.SendAsync("peer.screen.stop", new { role = "sharer" });
        Log?.Invoke($"peer: stopped sharing screen with {pcId}");
        ScreenSharingChanged?.Invoke(pcId, false);
        PeersChanged?.Invoke();
    }

    private void EndScreenIn(string pcId, string reason, bool notifyPeer)
    {
        EndControlIn(pcId, notifyPeer: false);
        bool was;
        lock (_screenOut) was = _screenIn.Remove(pcId);
        if (!was) return;
        if (notifyPeer) _ = Current(pcId)?.SendAsync("peer.screen.stop", new { role = "viewer" });
        ScreenViewEnded?.Invoke(pcId, reason);
        PeersChanged?.Invoke();
    }

    /// <summary>Sends each file in turn, streamed in 512 KB chunks so size isn't limited by memory
    /// or by the 8 MB frame cap. Chat messages still get through between chunks.</summary>
    public async Task<bool> SendFilesAsync(string pcId, IReadOnlyList<string> paths)
    {
        if (Current(pcId) == null) return false;
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            await SendOneFileAsync(pcId, path).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Stops a transfer in either direction; the other side is told too.</summary>
    public void CancelTransfer(string pcId, string transferId)
    {
        lock (_outgoing) { if (_outgoing.TryGetValue(transferId, out var o)) o.Canceled = true; }
        var conn = Current(pcId);
        if (conn != null && conn.Incoming.ContainsKey(transferId))
        {
            FinishIncoming(pcId, conn, transferId, "canceled");
            _ = conn.SendAsync("peer.file.cancel", new { transferId });
        }
    }

    /// <summary>Asks every NotiLink PC on the local network(s) to announce itself (the same UDP
    /// discovery the phone uses, port 45211). Takes ~1.5 s.</summary>
    public async Task<IReadOnlyList<DiscoveredPc>> ScanAsync()
    {
        var found = new Dictionary<string, DiscoveredPc>();
        var known = _store.List().Select(p => p.PcId).ToHashSet();
        try
        {
            using var udp = new UdpClient(0) { EnableBroadcast = true };
            var probe = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "notilink.discover", from = "pc" }));
            foreach (var target in BroadcastAddresses())
            {
                try { await udp.SendAsync(probe, probe.Length, new IPEndPoint(target, DiscoveryResponder.DiscoveryPort)).ConfigureAwait(false); }
                catch (SocketException) { }
            }
            using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            while (!window.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(window.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try
                {
                    using var doc = JsonDocument.Parse(result.Buffer);
                    var root = doc.RootElement;
                    if (Str(root, "type") != "notilink.announce") continue;
                    var pcId = Str(root, "pcId");
                    if (string.IsNullOrEmpty(pcId) || pcId == MyPcId) continue; // older build, or ourselves
                    var ip = result.RemoteEndPoint.Address.ToString();
                    if (!found.ContainsKey(pcId))
                        found[pcId] = new DiscoveredPc(pcId, Str(root, "pcName") ?? ip, ip, known.Contains(pcId));
                }
                catch (JsonException) { }
            }
        }
        catch (SocketException ex)
        {
            Log?.Invoke($"peer scan failed: {ex.Message}");
        }
        return found.Values.ToList();
    }

    // ---------------------------------------------------------------- connections

    /// <summary>TcpServer's hand-off: [client]'s first frame was peer.hello ([hello] is its payload).</summary>
    public void AcceptIncoming(TcpClient client, JsonElement hello, string remote)
    {
        var ip = remote.Contains(']') ? remote[1..remote.IndexOf(']')] : remote.Split(':')[0];
        var conn = new PeerConnection(client, ip, initiatorId: Str(hello, "pcId") ?? "");
        _ = Task.Run(async () =>
        {
            await HandleHelloAsync(conn, hello).ConfigureAwait(false);
            await ReadLoopAsync(conn).ConfigureAwait(false);
        });
    }

    private async Task HandleHelloAsync(PeerConnection conn, JsonElement hello)
    {
        var pcId = Str(hello, "pcId") ?? "";
        var name = Str(hello, "name") ?? conn.RemoteIp;
        if (pcId.Length == 0 || pcId == MyPcId) { conn.Close(); return; }

        if (_store.IsValidToken(pcId, Str(hello, "token")))
        {
            await conn.SendAsync("peer.welcome", new { pcId = MyPcId, name = MyName }).ConfigureAwait(false);
            RegisterConnection(conn, pcId);
            return;
        }

        // Unknown (or forgotten) PC: show a code here that has to be typed over there.
        conn.PendingPeerId = pcId;
        conn.PendingPeerName = name;
        conn.PendingCode = Random.Shared.Next(0, 1_000_000).ToString("D6");
        var previous = Interlocked.Exchange(ref _pairingIncoming, conn);
        if (previous != null && previous != conn) previous.Close();
        await conn.SendAsync("peer.pair.required", new { pcId = MyPcId, name = MyName }).ConfigureAwait(false);
        Log?.Invoke($"peer: pairing request from {name} ({conn.RemoteIp})");
        PairCodeShown?.Invoke(name, conn.PendingCode);
    }

    private async Task<PeerConnection?> DialAsync(string ip, bool userInitiated, string? expectedPcId = null)
    {
        var client = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(IPAddress.Parse(ip), _port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or FormatException)
        {
            client.Dispose();
            return null;
        }

        var conn = new PeerConnection(client, ip, initiatorId: MyPcId)
        {
            UserInitiated = userInitiated,
            ExpectedPeerId = expectedPcId,
        };
        if (userInitiated) _pairingOutgoing = conn;
        var token = expectedPcId != null ? _store.Get(expectedPcId)?.Token : null;
        await conn.SendAsync("peer.hello", new { pcId = MyPcId, name = MyName, token }).ConfigureAwait(false);
        _ = Task.Run(() => ReadLoopAsync(conn));
        return conn;
    }

    private void RegisterConnection(PeerConnection conn, string pcId)
    {
        PeerConnection? toClose = null;
        lock (_connections)
        {
            conn.PeerId = pcId;
            _needsRepair.Remove(pcId);
            if (_connections.TryGetValue(pcId, out var existing) && existing != conn && !existing.IsClosed)
            {
                var preferred = string.CompareOrdinal(MyPcId, pcId) < 0 ? MyPcId : pcId;
                var keepNew = existing.InitiatorId == conn.InitiatorId || conn.InitiatorId == preferred;
                if (keepNew) { toClose = existing; _connections[pcId] = conn; }
                else toClose = conn;
            }
            else
            {
                _connections[pcId] = conn;
            }
        }
        toClose?.Close();
        if (toClose == conn) return;

        var addresses = _myAddresses();
        _ = conn.SendAsync("peer.addresses", new { addresses });
        _store.Update(pcId, p => p with { Addresses = MergeAddresses(conn.RemoteIp, p.Addresses), LastSeen = Now() });
        Log?.Invoke($"peer: linked with {pcId} via {conn.RemoteIp}");
        PeersChanged?.Invoke();
    }

    private async Task ReadLoopAsync(PeerConnection conn)
    {
        var header = new byte[FrameCodec.HeaderLength];
        try
        {
            while (!_cts.IsCancellationRequested && !conn.IsClosed)
            {
                if (!await ReadExactAsync(conn.Stream, header, header.Length).ConfigureAwait(false)) break;
                conn.LastSeenUtc = DateTime.UtcNow;
                var (length, type) = FrameCodec.DecodeHeader(header);
                if (length < 0 || length > MaxFrameLength) break;
                var payload = new byte[length];
                if (!await ReadExactAsync(conn.Stream, payload, length).ConfigureAwait(false)) break;

                if (type == FrameCodec.TypeBinary)
                {
                    if (conn.PeerId is { } screenPeer && conn.ExpectScreenFrame)
                    {
                        conn.ExpectScreenFrame = false;
                        // Acked on arrival (the viewer window only ever draws the newest frame).
                        _ = conn.SendAsync("peer.screen.ack", new { });
                        bool viewing;
                        lock (_screenOut) viewing = _screenIn.Contains(screenPeer);
                        if (viewing) ScreenFrameReceived?.Invoke(screenPeer, payload);
                        continue;
                    }
                    if (conn.PeerId != null && conn.ExpectChunkFor is { } tid && conn.Incoming.TryGetValue(tid, out var rx))
                    {
                        conn.ExpectChunkFor = null;
                        WriteChunk(conn.PeerId, conn, tid, rx, payload);
                    }
                    continue;
                }
                if (type != FrameCodec.TypeJson) continue;

                string msgType;
                JsonElement body;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    msgType = Str(doc.RootElement, "type") ?? "";
                    body = doc.RootElement.TryGetProperty("payload", out var p) ? p.Clone() : default;
                }
                catch (JsonException) { continue; }

                await HandleMessageAsync(conn, msgType, body).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
        finally
        {
            conn.Close();
            OnConnectionClosed(conn);
        }
    }

    private async Task HandleMessageAsync(PeerConnection conn, string type, JsonElement p)
    {
        // ---- pairing / handshake (before the connection belongs to a peer)
        switch (type)
        {
            case "peer.welcome":
            {
                var pcId = Str(p, "pcId") ?? "";
                if (pcId.Length == 0 || (conn.ExpectedPeerId != null && conn.ExpectedPeerId != pcId)) { conn.Close(); return; }
                if (_store.Get(pcId) == null) { conn.Close(); return; } // it trusts us but we forgot it
                if (conn.UserInitiated) { Interlocked.CompareExchange(ref _pairingOutgoing, null, conn); PairResult?.Invoke(true, $"Conectado con {_store.Get(pcId)!.Name}."); }
                RegisterConnection(conn, pcId);
                return;
            }
            case "peer.pair.required":
                if (!conn.UserInitiated)
                {
                    // Background reconnect, but that PC no longer recognizes us — needs a new code.
                    if (conn.ExpectedPeerId != null) lock (_connections) _needsRepair.Add(conn.ExpectedPeerId);
                    PeersChanged?.Invoke();
                    conn.Close();
                    return;
                }
                conn.PendingPeerName = Str(p, "name") ?? conn.RemoteIp;
                PairCodeNeeded?.Invoke(conn.PendingPeerName);
                return;
            case "peer.pair.code":
                await HandlePairCodeAsync(conn, Str(p, "code") ?? "").ConfigureAwait(false);
                return;
            case "peer.pair.ok":
            {
                var pcId = Str(p, "pcId") ?? "";
                var token = Str(p, "token") ?? "";
                if (pcId.Length == 0 || token.Length == 0) { conn.Close(); return; }
                var name = Str(p, "name") ?? conn.PendingPeerName ?? conn.RemoteIp;
                var existingName = _store.Get(pcId)?.Name;
                _store.Save(new PairedPeer(pcId, existingName ?? name, token, new List<string> { conn.RemoteIp }, Now()));
                Interlocked.CompareExchange(ref _pairingOutgoing, null, conn);
                PairResult?.Invoke(true, $"PC vinculada: {existingName ?? name}");
                RegisterConnection(conn, pcId);
                return;
            }
            case "peer.pair.fail":
            {
                var reason = Str(p, "reason");
                var left = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("attemptsLeft", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                PairResult?.Invoke(false, reason == "rejected"
                    ? "La otra PC rechazó la solicitud."
                    : left > 0 ? $"Código incorrecto. Te quedan {left} intento(s)." : "Código incorrecto. Vuelve a empezar.");
                if (left <= 0) { Interlocked.CompareExchange(ref _pairingOutgoing, null, conn); conn.Close(); }
                return;
            }
        }

        var peerId = conn.PeerId;
        if (peerId == null || Current(peerId) != conn) return; // not (or no longer) the live link

        switch (type)
        {
            case "peer.ping":
                return;
            case "peer.addresses":
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("addresses", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var theirs = arr.EnumerateArray().Select(x => x.GetString()).OfType<string>().Where(x => IPAddress.TryParse(x, out _)).ToList();
                    _store.Update(peerId, x => x with { Addresses = MergeAddresses(conn.RemoteIp, theirs.Concat(x.Addresses)) });
                }
                return;
            case "peer.message":
            {
                var kind = Str(p, "kind") == "clipboard" ? "clipboard" : "text";
                var text = Str(p, "text") ?? "";
                if (text.Length == 0) return;
                var item = new PeerChatItem { Id = Str(p, "id") ?? Guid.NewGuid().ToString("N"), Outgoing = false, Kind = kind, Text = text, At = Now() };
                _chat.Upsert(peerId, item);
                ChatItemChanged?.Invoke(peerId, item);
                if (kind == "clipboard") ClipboardReceived?.Invoke(peerId, text);
                return;
            }
            case "peer.nudge":
            {
                var item = new PeerChatItem { Id = Guid.NewGuid().ToString("N"), Outgoing = false, Kind = "nudge", At = Now() };
                _chat.Upsert(peerId, item);
                ChatItemChanged?.Invoke(peerId, item);
                NudgeReceived?.Invoke(peerId, _store.Get(peerId)?.Name ?? "Otra PC");
                return;
            }
            case "peer.unpair":
                EndScreenOut(peerId, notifyPeer: false);
                EndScreenIn(peerId, "disconnected", notifyPeer: false);
                _store.Remove(peerId);
                lock (_connections) _connections.Remove(peerId);
                conn.Close();
                PeersChanged?.Invoke();
                return;
            case "peer.screen.request":
                if (_store.Get(peerId)?.AllowScreen == true) await ShareScreenAsync(peerId).ConfigureAwait(false);
                else ScreenRequested?.Invoke(peerId, _store.Get(peerId)?.Name ?? "Otra PC");
                return;
            case "peer.screen.start":
            {
                bool isNew;
                lock (_screenOut) isNew = _screenIn.Add(peerId);
                if (!isNew) return;
                ScreenViewStarted?.Invoke(peerId, _store.Get(peerId)?.Name ?? "Otra PC");
                PeersChanged?.Invoke();
                return;
            }
            case "peer.screen.frame":
                conn.ExpectScreenFrame = true;
                return;
            case "peer.screen.ack":
                lock (_screenOut)
                {
                    if (_screenOut.TryGetValue(peerId, out var inFlight) && inFlight > 0) _screenOut[peerId] = inFlight - 1;
                }
                return;
            case "peer.screen.denied":
                ScreenViewEnded?.Invoke(peerId, "denied");
                return;
            case "peer.screen.stop":
                // role = the sender's side of it: a viewer leaving, or a sharer ending.
                if (Str(p, "role") != "sharer") EndScreenOut(peerId, notifyPeer: false);
                if (Str(p, "role") != "viewer") EndScreenIn(peerId, "stopped", notifyPeer: false);
                return;
            case "peer.control.request":
            {
                bool sharing;
                lock (_screenOut) sharing = _screenOut.ContainsKey(peerId);
                if (!sharing) { await conn.SendAsync("peer.control.denied", new { }).ConfigureAwait(false); return; }
                if (_store.Get(peerId)?.AllowControl == true) GrantControl(peerId);
                else ControlRequested?.Invoke(peerId, _store.Get(peerId)?.Name ?? "Otra PC");
                return;
            }
            case "peer.control.granted":
            {
                bool added;
                lock (_screenOut) added = _screenIn.Contains(peerId) && _controlIn.Add(peerId);
                if (!added) return;
                ControllingChanged?.Invoke(peerId, true);
                PeersChanged?.Invoke();
                return;
            }
            case "peer.control.denied":
                ControlDenied?.Invoke(peerId);
                return;
            case "peer.control.stop":
                if (Str(p, "role") != "sharer") EndControlOut(peerId, notifyPeer: false);
                if (Str(p, "role") != "viewer") EndControlIn(peerId, notifyPeer: false);
                return;
            case "peer.input":
            {
                bool allowed;
                lock (_screenOut) allowed = _controlOut.Contains(peerId);
                if (allowed && ParseInput(p) is { } input) InputReceived?.Invoke(peerId, input);
                return;
            }
            case "peer.file.offer":
                StartIncoming(peerId, conn, p);
                return;
            case "peer.file.chunk":
                conn.ExpectChunkFor = Str(p, "transferId");
                return;
            case "peer.file.done":
                if (Str(p, "transferId") is { } doneId) FinishIncoming(peerId, conn, doneId, "done");
                return;
            case "peer.file.cancel":
                if (Str(p, "transferId") is { } cancelId)
                {
                    lock (_outgoing) { if (_outgoing.TryGetValue(cancelId, out var o)) o.Canceled = true; }
                    if (conn.Incoming.ContainsKey(cancelId)) FinishIncoming(peerId, conn, cancelId, "canceled");
                }
                return;
        }
    }

    private async Task HandlePairCodeAsync(PeerConnection conn, string code)
    {
        if (conn.PendingCode == null || conn != _pairingIncoming) return;
        if (code == conn.PendingCode)
        {
            var pcId = conn.PendingPeerId!;
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            var name = _store.Get(pcId)?.Name ?? conn.PendingPeerName ?? conn.RemoteIp;
            _store.Save(new PairedPeer(pcId, name, token, new List<string> { conn.RemoteIp }, Now()));
            conn.PendingCode = null;
            Interlocked.CompareExchange(ref _pairingIncoming, null, conn);
            await conn.SendAsync("peer.pair.ok", new { pcId = MyPcId, name = MyName, token }).ConfigureAwait(false);
            PairCodeDismissed?.Invoke();
            PairResult?.Invoke(true, $"PC vinculada: {name}");
            RegisterConnection(conn, pcId);
            return;
        }

        conn.PairAttempts++;
        var left = MaxPairAttempts - conn.PairAttempts;
        await conn.SendAsync("peer.pair.fail", new { reason = "wrong_code", attemptsLeft = left }).ConfigureAwait(false);
        if (left <= 0)
        {
            Interlocked.CompareExchange(ref _pairingIncoming, null, conn);
            PairCodeDismissed?.Invoke();
            conn.Close();
        }
    }

    private void OnConnectionClosed(PeerConnection conn)
    {
        Interlocked.CompareExchange(ref _pairingOutgoing, null, conn);
        if (Interlocked.CompareExchange(ref _pairingIncoming, null, conn) == conn) PairCodeDismissed?.Invoke();

        var peerId = conn.PeerId;
        if (peerId == null) return;
        foreach (var tid in conn.Incoming.Keys.ToList()) FinishIncoming(peerId, conn, tid, "failed");

        bool wasCurrent;
        lock (_connections)
        {
            wasCurrent = _connections.TryGetValue(peerId, out var c) && c == conn;
            if (wasCurrent) _connections.Remove(peerId);
        }
        if (wasCurrent)
        {
            EndScreenOut(peerId, notifyPeer: false);
            EndScreenIn(peerId, "disconnected", notifyPeer: false);
            _store.Update(peerId, p => p with { LastSeen = Now() });
            Log?.Invoke($"peer: {peerId} disconnected");
            PeersChanged?.Invoke();
        }
    }

    private PeerConnection? Current(string pcId)
    {
        lock (_connections) return _connections.TryGetValue(pcId, out var c) && !c.IsClosed ? c : null;
    }

    /// <summary>Every 15 s: ping every link, drop links silent for 60 s+, and redial known PCs that
    /// aren't connected (every other tick, ~30 s), with a LAN scan now and then for PCs whose
    /// saved IPs all stopped answering (DHCP gave them a new one).</summary>
    private void MaintenanceTick()
    {
        List<PeerConnection> live;
        lock (_connections) live = _connections.Values.ToList();
        var cutoff = DateTime.UtcNow - SilentTimeout;
        foreach (var c in live)
        {
            if (c.LastSeenUtc < cutoff) { Log?.Invoke($"peer: {c.PeerId} silent for {SilentTimeout.TotalSeconds:0}s, closing"); c.Close(); }
            else _ = c.SendAsync("peer.ping", new { });
        }

        _tick++;
        if (_tick % 2 != 1) return;
        _ = Task.Run(RedialAsync);
    }

    private async Task RedialAsync()
    {
        var offline = _store.List().Where(p =>
        {
            lock (_connections) return !_connections.ContainsKey(p.PcId) && !_needsRepair.Contains(p.PcId) && !_dialing.Contains(p.PcId);
        }).ToList();
        if (offline.Count == 0) return;

        if (_tick % 8 == 1)
        {
            // Refresh addresses from a LAN scan before dialing.
            foreach (var d in await ScanAsync().ConfigureAwait(false))
                if (offline.Any(o => o.PcId == d.PcId))
                    _store.Update(d.PcId, p => p with { Addresses = MergeAddresses(d.Ip, p.Addresses) });
            offline = offline.Select(o => _store.Get(o.PcId) ?? o).ToList();
        }

        foreach (var peer in offline)
        {
            lock (_connections) { if (!_dialing.Add(peer.PcId)) continue; }
            try
            {
                foreach (var ip in peer.Addresses.Take(6))
                {
                    if (Current(peer.PcId) != null) break;
                    var conn = await DialAsync(ip, userInitiated: false, expectedPcId: peer.PcId).ConfigureAwait(false);
                    if (conn == null) continue;
                    // Give the handshake a moment; stop trying other IPs once it's linked.
                    for (var i = 0; i < 20 && Current(peer.PcId) == null && !conn.IsClosed; i++) await Task.Delay(100).ConfigureAwait(false);
                    if (Current(peer.PcId) != null) break;
                    conn.Close();
                }
            }
            finally
            {
                lock (_connections) _dialing.Remove(peer.PcId);
            }
        }
    }

    // ---------------------------------------------------------------- file transfer

    private sealed class OutgoingTransfer { public volatile bool Canceled; }

    private sealed class IncomingTransfer
    {
        public required PeerChatItem Item;
        public required FileStream Stream;
        public required string PartPath;
        public DateTime LastReport;
    }

    private async Task SendOneFileAsync(string pcId, string path)
    {
        var info = new FileInfo(path);
        var transferId = Guid.NewGuid().ToString("N");
        var item = new PeerChatItem
        {
            Id = transferId, Outgoing = true, Kind = "file", At = Now(),
            FileName = info.Name, FileSize = info.Length, State = "sending", Path = path,
        };
        var state = new OutgoingTransfer();
        lock (_outgoing) _outgoing[transferId] = state;
        _chat.Upsert(pcId, item);
        ChatItemChanged?.Invoke(pcId, item);

        try
        {
            var conn = Current(pcId);
            if (conn == null) { item.State = "failed"; return; }
            await conn.SendAsync("peer.file.offer", new { transferId, fileName = info.Name, size = info.Length }).ConfigureAwait(false);

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileChunkSize, useAsync: true);
            var buffer = new byte[FileChunkSize];
            var lastReport = DateTime.UtcNow;
            while (true)
            {
                if (state.Canceled) { item.State = "canceled"; await conn.SendAsync("peer.file.cancel", new { transferId }).ConfigureAwait(false); return; }
                if (Current(pcId) != conn) { item.State = "failed"; return; }
                var read = await fs.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) break;
                var chunk = read == buffer.Length ? buffer : buffer[..read];
                if (!await conn.SendWithBinaryAsync("peer.file.chunk", new { transferId }, chunk).ConfigureAwait(false)) { item.State = "failed"; return; }
                item.Transferred += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.UtcNow;
                    ChatItemChanged?.Invoke(pcId, item);
                }
            }
            await conn.SendAsync("peer.file.done", new { transferId }).ConfigureAwait(false);
            item.State = "done";
        }
        catch (IOException ex)
        {
            Log?.Invoke($"peer: sending {path} failed: {ex.Message}");
            item.State = "failed";
        }
        finally
        {
            lock (_outgoing) _outgoing.Remove(transferId);
            _chat.Upsert(pcId, item);
            ChatItemChanged?.Invoke(pcId, item);
        }
    }

    private void StartIncoming(string pcId, PeerConnection conn, JsonElement p)
    {
        var transferId = Str(p, "transferId");
        if (transferId == null || conn.Incoming.ContainsKey(transferId)) return;
        var fileName = SafeFileName(Str(p, "fileName") ?? "archivo");
        var size = p.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        var item = new PeerChatItem
        {
            Id = transferId, Outgoing = false, Kind = "file", At = Now(),
            FileName = fileName, FileSize = size, State = "receiving",
        };
        try
        {
            var dir = DownloadsDir();
            Directory.CreateDirectory(dir);
            var part = Path.Combine(dir, $"{fileName}.{transferId[..8]}.notilink-part");
            var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            conn.Incoming[transferId] = new IncomingTransfer { Item = item, Stream = fs, PartPath = part, LastReport = DateTime.UtcNow };
        }
        catch (IOException ex)
        {
            Log?.Invoke($"peer: can't receive {fileName}: {ex.Message}");
            item.State = "failed";
            _ = conn.SendAsync("peer.file.cancel", new { transferId });
        }
        _chat.Upsert(pcId, item);
        ChatItemChanged?.Invoke(pcId, item);
    }

    private void WriteChunk(string pcId, PeerConnection conn, string transferId, IncomingTransfer rx, byte[] data)
    {
        try
        {
            rx.Stream.Write(data);
            rx.Item.Transferred += data.Length;
            if ((DateTime.UtcNow - rx.LastReport).TotalMilliseconds > 250)
            {
                rx.LastReport = DateTime.UtcNow;
                ChatItemChanged?.Invoke(pcId, rx.Item);
            }
        }
        catch (IOException ex)
        {
            Log?.Invoke($"peer: writing {rx.Item.FileName} failed: {ex.Message}");
            FinishIncoming(pcId, conn, transferId, "failed");
            _ = conn.SendAsync("peer.file.cancel", new { transferId });
        }
    }

    private void FinishIncoming(string pcId, PeerConnection conn, string transferId, string outcome)
    {
        if (!conn.Incoming.TryRemove(transferId, out var rx)) return;
        try { rx.Stream.Dispose(); } catch (IOException) { }
        if (outcome == "done")
        {
            try
            {
                var final = UniquePath(Path.Combine(DownloadsDir(), rx.Item.FileName!));
                File.Move(rx.PartPath, final);
                rx.Item.Path = final;
            }
            catch (IOException ex)
            {
                Log?.Invoke($"peer: finishing {rx.Item.FileName} failed: {ex.Message}");
                outcome = "failed";
            }
        }
        if (outcome != "done") { try { File.Delete(rx.PartPath); } catch (IOException) { } }
        rx.Item.State = outcome;
        _chat.Upsert(pcId, rx.Item);
        ChatItemChanged?.Invoke(pcId, rx.Item);
    }

    private string DownloadsDir() => _downloadsDir;

    private static string SafeFileName(string name)
    {
        var safe = string.Concat(Path.GetFileName(name).Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        return safe.Length == 0 ? "archivo" : safe;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static List<string> MergeAddresses(string first, IEnumerable<string> rest) =>
        new[] { first }.Concat(rest).Where(a => !string.IsNullOrEmpty(a)).Distinct().Take(8).ToList();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>255.255.255.255 plus each up adapter's directed broadcast — the limited broadcast
    /// alone only leaves through one adapter on multi-homed PCs.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 255)) continue;
                    var bcast = new byte[4];
                    for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(bcast));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list.Distinct();
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset)).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        List<PeerConnection> all;
        lock (_connections) all = _connections.Values.ToList();
        foreach (var c in all) c.Close();
        _pairingIncoming?.Close();
        _pairingOutgoing?.Close();
    }

    /// <summary>One TCP link to another PC (either direction).</summary>
    private sealed class PeerConnection
    {
        private readonly TcpClient _client;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _closed;

        public PeerConnection(TcpClient client, string remoteIp, string initiatorId)
        {
            _client = client;
            _client.NoDelay = true;
            Stream = client.GetStream();
            RemoteIp = remoteIp;
            InitiatorId = initiatorId;
        }

        public NetworkStream Stream { get; }
        public string RemoteIp { get; }
        public string InitiatorId { get; }
        public string? PeerId { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public bool IsClosed => Volatile.Read(ref _closed) == 1;

        public bool UserInitiated { get; init; }
        public string? ExpectedPeerId { get; init; }
        public string? PendingPeerId { get; set; }
        public string? PendingPeerName { get; set; }
        public string? PendingCode { get; set; }
        public int PairAttempts { get; set; }

        public string? ExpectChunkFor { get; set; }
        public bool ExpectScreenFrame { get; set; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, IncomingTransfer> Incoming { get; } = new();

        public Task<bool> SendAsync(string type, object payload) => WriteAsync(Envelope(type, payload), null);

        public Task<bool> SendWithBinaryAsync(string type, object payload, byte[] bytes) =>
            WriteAsync(Envelope(type, payload), FrameCodec.EncodeBinary(bytes));

        private static byte[] Envelope(string type, object payload) => FrameCodec.EncodeJson(JsonSerializer.Serialize(new
        {
            v = 1,
            type,
            id = Guid.NewGuid().ToString(),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload,
        }));

        private async Task<bool> WriteAsync(byte[] json, byte[]? binary)
        {
            if (IsClosed) return false;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(json).ConfigureAwait(false);
                if (binary != null) await Stream.WriteAsync(binary).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                Close();
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            try { _client.Close(); } catch (Exception) { /* already gone */ }
        }
    }
}
