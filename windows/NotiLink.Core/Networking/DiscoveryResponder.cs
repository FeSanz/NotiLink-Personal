using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NotiLink.Core.Networking;

/// <summary>
/// UDP side of pairing per docs/PROTOCOL.md: listens for a phone's
/// "notilink.discover" broadcast and replies unicast with this PC's TCP
/// port, so the phone can connect without the user typing an IP.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    public const int DiscoveryPort = 45211;

    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    public event Action<string>? Log;

    public void Start(string pcName, int tcpPort, string? pcId = null)
    {
        _cts = new CancellationTokenSource();
        _client = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
        _ = ListenLoopAsync(pcName, tcpPort, pcId, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _client?.Close();
    }

    private async Task ListenLoopAsync(string pcName, int tcpPort, string? pcId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _client!.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "notilink.discover")
                    {
                        var reply = JsonSerializer.Serialize(new { type = "notilink.announce", pcName, tcpPort, pcId });
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await _client!.SendAsync(bytes, result.RemoteEndPoint, ct).ConfigureAwait(false);
                    }
                }
                catch (JsonException)
                {
                    // not a well-formed discovery packet — ignore
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException ex)
        {
            Log?.Invoke($"Discovery listener stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
