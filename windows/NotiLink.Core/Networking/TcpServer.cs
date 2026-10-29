using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NotiLink.Core.Protocol;

namespace NotiLink.Core.Networking;

/// <summary>
/// PC-side TCP server per docs/PROTOCOL.md — phones always connect in as the
/// client, and several can be connected at once (multiple paired devices).
/// A connection's deviceId is only known once its handshake.hello (always
/// the first frame) has been parsed; every event and every SendAsync after
/// that point is keyed by deviceId so state never gets attributed to the
/// wrong phone. Accepts connections, parses frames, surfaces JSON messages
/// and correlates binary image/icon frames to the JSON message that
/// referenced them.
/// </summary>
public sealed class TcpServer : IDisposable
{
    public const int DefaultPort = 45212;
    private const int MaxFrameLength = 8 * 1024 * 1024;

    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, NetworkStream> _streamsByDeviceId = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // A phone that silently vanishes from the network (left WiFi range, airplane mode, ...)
    // never sends a TCP FIN/RST — the blocking ReadAsync() in HandleClientAsync just hangs with
    // no error, for as long as the OS itself is willing to wait on a dead socket with no
    // keepalive configured (which can be a very long time). DeviceDisconnected alone never fired
    // for that case, so nothing ever told a still-"connected" scrcpy screen/audio share to stop —
    // confirmed the hard way: a phone that left WiFi mid-audio-share left its own audio capture
    // stuck open, silencing the phone system-wide, until a reboot.
    //
    // Deliberately generous (was 45s originally, based on the phone's own 8s heartbeat cadence —
    // that math was too optimistic in practice). This app's foreground service on some OEMs
    // (Samsung's "sleeping apps"/adaptive battery in particular — see CLAUDE.md's own rough-edge
    // note about it) can genuinely delay a background ScheduledExecutorService well past its
    // nominal interval without the connection itself being remotely unhealthy — confirmed live as
    // a real "se conecta y desconecta a cada rato" regression right after 45s first shipped. This
    // watchdog only exists to catch the case above (a phone that's genuinely, permanently gone);
    // it doesn't need to be tight to do that job, it just needs to eventually fire instead of
    // literally never — 3 minutes is still a enormous improvement over "stuck until reboot".
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);
    private readonly Dictionary<string, DateTime> _lastSeenUtc = new();
    private System.Threading.Timer? _watchdogTimer;

    public event Action<string, string>? DeviceConnected;    // deviceId, remoteEndpoint
    public event Action<string>? DeviceDisconnected;         // deviceId
    public event Action<string, string, JsonElement>? MessageReceived; // deviceId, type, payload
    /// <summary>Fires when a binary (0x02) frame arrives, paired with the transferId of
    /// whichever preceding JSON message referenced it (notification.posted's imageTransferId,
    /// or icon.data's transferId) — binary frames always immediately follow their message.</summary>
    public event Action<string, string, byte[]>? ImageReceived; // deviceId, transferId, bytes
    public event Action<string>? Log;

    /// <summary>PC ↔ PC (see PeerLinkService): a connection whose first frame is peer.hello is
    /// another NotiLink PC, not a phone — it's handed over (client, hello payload, remote
    /// endpoint) and this server stops reading from it. Sharing the phones' port means any
    /// firewall/Tailscale setup that already lets a phone in works for another PC too.</summary>
    public Action<TcpClient, JsonElement, string>? PeerHandoff { get; set; }

    public TcpServer(int port = DefaultPort)
    {
        _port = port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Log?.Invoke($"Listening on 0.0.0.0:{_port}");
        _ = AcceptLoopAsync(_cts.Token);
        _watchdogTimer = new System.Threading.Timer(_ => MaintenanceTick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    /// <summary>Every 10s: (1) sends this PC's own heartbeat to every connected phone, and (2)
    /// force-closes any device whose connection has gone silent for longer than HeartbeatTimeout.
    /// Both matter together — see the doc comment on HeartbeatTimeout above. (1) specifically was
    /// a real, confirmed regression: the phone's own read timeout (added alongside (2) so a dead
    /// connection is detected on *both* ends, not just the PC's) needs SOMETHING arriving from
    /// the PC periodically to reset against — the PC previously never sent anything on its own
    /// (only ever reacted to the phone's own heartbeat/messages), so an otherwise perfectly
    /// healthy connection with no PC-initiated activity for 60s+ (e.g. no deletes/actions/volume
    /// changes — completely normal while just glancing at the phone) tripped the phone's own
    /// timeout and reconnected for no real reason. Confirmed live as a "se conecta y desconecta a
    /// cada rato" report right after that phone-side timeout was added. `docs/PROTOCOL.md`
    /// describes heartbeat as bidirectional — this is what actually makes that true; it wasn't
    /// before.
    /// The read loop's own catch/finally does the actual disconnect cleanup and fires
    /// DeviceDisconnected, exactly like a real socket-level disconnect, so every downstream
    /// consumer (screen/audio share teardown included) reacts the same way regardless of which
    /// kind of disconnect it was.</summary>
    private void MaintenanceTick()
    {
        var cutoff = DateTime.UtcNow - HeartbeatTimeout;
        List<string> connected;
        List<NetworkStream> stale = new();
        lock (_streamsByDeviceId)
        {
            connected = _streamsByDeviceId.Keys.ToList();
            foreach (var (deviceId, lastSeen) in _lastSeenUtc)
            {
                if (lastSeen < cutoff && _streamsByDeviceId.TryGetValue(deviceId, out var s))
                {
                    Log?.Invoke($"{deviceId}: no data received in over {HeartbeatTimeout.TotalSeconds:0}s, treating as disconnected.");
                    stale.Add(s);
                }
            }
        }
        foreach (var deviceId in connected)
        {
            _ = SendAsync(deviceId, "heartbeat", new { });
        }
        // Closed outside the lock — Close() synchronously runs the blocked ReadAsync's
        // IOException/finally cleanup path inline in some cases, which itself needs the lock.
        foreach (var stream in stale)
        {
            try { stream.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                _ = HandleClientAsync(client, remote, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleClientAsync(TcpClient client, string remote, CancellationToken ct)
    {
        var handedOff = false;
        try
        {
            var stream = client.GetStream();
            var header = new byte[FrameCodec.HeaderLength];
            var pendingTransferIds = new Queue<string>();
            string? deviceId = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header, FrameCodec.HeaderLength, ct).ConfigureAwait(false))
                        break;

                    // Any real bytes at all count as "alive" for the watchdog above, not just a
                    // parsed heartbeat.request — an image/notification frame proves the
                    // connection just as well and shouldn't need its own separate tracking.
                    if (deviceId != null) lock (_streamsByDeviceId) { _lastSeenUtc[deviceId] = DateTime.UtcNow; }

                    var (length, type) = FrameCodec.DecodeHeader(header);

                    if (length < 0 || length > MaxFrameLength)
                    {
                        Log?.Invoke($"[{remote}] frame length {length} exceeds max ({MaxFrameLength}), closing connection.");
                        break;
                    }

                    var payload = new byte[length];
                    if (!await ReadExactAsync(stream, payload, length, ct).ConfigureAwait(false))
                        break;

                    if (type == FrameCodec.TypeJson)
                    {
                        string msgType;
                        JsonElement payloadEl;
                        try
                        {
                            var json = Encoding.UTF8.GetString(payload);
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            msgType = root.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
                            payloadEl = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
                        }
                        catch (JsonException ex)
                        {
                            Log?.Invoke($"[{remote}] malformed JSON frame: {ex.Message}");
                            continue;
                        }

                        if (deviceId == null && msgType == "peer.hello" && PeerHandoff is { } handoff)
                        {
                            handedOff = true;
                            handoff(client, payloadEl, remote);
                            return;
                        }

                        if (deviceId == null)
                        {
                            // The protocol requires handshake.hello as the very first frame on
                            // every connection — anything else this early means a broken/rogue
                            // client, not a real phone, so the connection is simply dropped.
                            if (msgType != "handshake.hello" ||
                                !payloadEl.TryGetProperty("deviceId", out var idEl) ||
                                idEl.GetString() is not { Length: > 0 } id)
                            {
                                Log?.Invoke($"[{remote}] first frame wasn't a valid handshake.hello, dropping connection.");
                                break;
                            }
                            deviceId = id;
                            NetworkStream? replaced;
                            lock (_streamsByDeviceId)
                            {
                                _streamsByDeviceId.TryGetValue(deviceId, out replaced);
                                _streamsByDeviceId[deviceId] = stream;
                                _lastSeenUtc[deviceId] = DateTime.UtcNow;
                            }
                            // The phone just came back on a new socket — its previous one is dead
                            // weight; close it now instead of letting it linger until it times out.
                            if (replaced != null && !ReferenceEquals(replaced, stream))
                            {
                                try { replaced.Close(); } catch (Exception) { /* already gone */ }
                            }
                            DeviceConnected?.Invoke(deviceId, remote);
                        }

                        // A handler that throws must not take the whole connection down with it (the
                        // exception would escape this loop and the socket would be reset, which the
                        // phone sees as "connection abort" and answers by reconnecting in a loop).
                        try { MessageReceived?.Invoke(deviceId, msgType, payloadEl); }
                        catch (Exception ex) { Log?.Invoke($"[{remote}] handler for '{msgType}' threw {ex.GetType().Name}: {ex.Message} | {ex.StackTrace?.Replace(Environment.NewLine, " / ")}"); }
                        foreach (var transferId in ExtractExpectedTransferIds(msgType, payloadEl))
                            pendingTransferIds.Enqueue(transferId);
                    }
                    else if (type == FrameCodec.TypeBinary)
                    {
                        if (deviceId != null && pendingTransferIds.TryDequeue(out var expected))
                        {
                            ImageReceived?.Invoke(deviceId, expected, payload);
                        }
                        else
                        {
                            Log?.Invoke($"[{remote}] binary frame ({payload.Length} bytes) with no pending transferId, ignoring.");
                        }
                    }
                    else
                    {
                        Log?.Invoke($"[{remote}] unknown frame type 0x{type:X2}, ignoring.");
                    }
                }
            }
            catch (IOException ex)
            {
                // remote closed the socket mid-read; treated as a normal disconnect below
                Log?.Invoke($"[{remote}] {deviceId ?? "?"}: read ended ({ex.GetType().Name}: {ex.Message})");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[{remote}] {deviceId ?? "?"}: connection dropped by {ex.GetType().Name}: {ex.Message} | {ex.StackTrace?.Replace(Environment.NewLine, " / ")}");
            }
            finally
            {
                if (deviceId != null)
                {
                    // Only the device's CURRENT connection ending means it's actually gone. A phone
                    // that switched networks reconnects on a fresh socket while its old, half-open
                    // one is still waiting to time out — when that stale loop finally ends here, the
                    // phone is very much still connected (and still sending notifications) on the
                    // new socket. Firing DeviceDisconnected for it anyway is exactly what left the PC
                    // saying "no conectado" while notifications kept arriving.
                    bool wasCurrent;
                    lock (_streamsByDeviceId)
                    {
                        wasCurrent = _streamsByDeviceId.TryGetValue(deviceId, out var s) && ReferenceEquals(s, stream);
                        if (wasCurrent)
                        {
                            _streamsByDeviceId.Remove(deviceId);
                            _lastSeenUtc.Remove(deviceId);
                        }
                    }
                    if (wasCurrent) DeviceDisconnected?.Invoke(deviceId);
                    else Log?.Invoke($"[{remote}] stale connection for {deviceId} closed; a newer one is still active.");
                }
            }
        }
        finally
        {
            if (!handedOff) client.Dispose();
        }
    }

    /// <summary>
    /// Sends a command to one specific paired phone (delete.request, action.invoke,
    /// app.filter.update, ...). No-ops with a log line if that device isn't currently
    /// connected — commands are never silently misrouted to a different phone.
    /// </summary>
    public async Task SendAsync(string deviceId, string type, object payload)
    {
        NetworkStream? stream;
        lock (_streamsByDeviceId) { _streamsByDeviceId.TryGetValue(deviceId, out stream); }
        if (stream == null)
        {
            Log?.Invoke($"SendAsync('{type}') to {deviceId}: not connected, dropping.");
            return;
        }

        var envelope = new
        {
            v = 1,
            type,
            id = Guid.NewGuid().ToString(),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload,
        };
        var frame = FrameCodec.EncodeJson(JsonSerializer.Serialize(envelope));

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log?.Invoke($"SendAsync('{type}') to {deviceId} failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Same as SendAsync, plus one binary (0x02) frame immediately after — the
    /// PC->phone counterpart of how the phone already sends icon/avatar/message images to the
    /// PC, used for file.send (see docs/PROTOCOL.md). The receiving side always associates a
    /// binary frame with the transferId of the JSON message that immediately preceded it.</summary>
    public async Task SendWithBinaryAsync(string deviceId, string type, object payload, byte[] bytes)
    {
        NetworkStream? stream;
        lock (_streamsByDeviceId) { _streamsByDeviceId.TryGetValue(deviceId, out stream); }
        if (stream == null)
        {
            Log?.Invoke($"SendWithBinaryAsync('{type}') to {deviceId}: not connected, dropping.");
            return;
        }

        var envelope = new
        {
            v = 1,
            type,
            id = Guid.NewGuid().ToString(),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload,
        };
        var jsonFrame = FrameCodec.EncodeJson(JsonSerializer.Serialize(envelope));
        var binaryFrame = FrameCodec.EncodeBinary(bytes);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(jsonFrame).ConfigureAwait(false);
            await stream.WriteAsync(binaryFrame).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log?.Invoke($"SendWithBinaryAsync('{type}') to {deviceId} failed: {ex.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Force-closes one device's socket right now (e.g. right after revoking its
    /// pairing) — the read loop's own catch/finally handles cleanup and fires
    /// DeviceDisconnected, same as any other disconnect. No-ops if it isn't connected.</summary>
    public void Disconnect(string deviceId)
    {
        NetworkStream? stream;
        lock (_streamsByDeviceId) { _streamsByDeviceId.TryGetValue(deviceId, out stream); }
        try { stream?.Close(); } catch (IOException) { }
    }

    private static IEnumerable<string> ExtractExpectedTransferIds(string msgType, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) yield break;

        if (msgType is "icon.data" or "wallpaper.data" or "file.send")
        {
            if (payload.TryGetProperty("transferId", out var tid) && tid.GetString() is { } s)
                yield return s;
            yield break;
        }

        // media.state's album art was being dropped silently: this message type wasn't
        // recognized at all here, so its albumArtTransferId never got queued, and the binary
        // frame that followed always fell into the "no pending transferId, ignoring" branch —
        // confirmed via on-device logs showing the phone successfully resolving and sending the
        // art every time (hasArt=true), while the PC never received/rendered it.
        if (msgType == "media.state")
        {
            if (payload.TryGetProperty("albumArtTransferId", out var artTid) &&
                artTid.ValueKind == JsonValueKind.String && artTid.GetString() is { } art)
                yield return art;
            yield break;
        }

        if (msgType != "notification.posted") yield break;

        // Fixed order: avatar, then the top-level bigPicture image, then each message's image.
        if (payload.TryGetProperty("hasAvatar", out var hasAvatar) && hasAvatar.GetBoolean() &&
            payload.TryGetProperty("avatarTransferId", out var avatarTid) && avatarTid.GetString() is { } a)
            yield return a;

        if (payload.TryGetProperty("hasImage", out var hasImg) && hasImg.GetBoolean() &&
            payload.TryGetProperty("imageTransferId", out var imgTid) && imgTid.GetString() is { } i)
            yield return i;

        if (payload.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in messages.EnumerateArray())
            {
                if (m.TryGetProperty("hasImage", out var mHasImg) && mHasImg.GetBoolean() &&
                    m.TryGetProperty("imageTransferId", out var mTid) && mTid.GetString() is { } mi)
                    yield return mi;
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
