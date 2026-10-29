using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotiLink.Core.Storage;

/// <param name="Addresses">Every IP this PC was last reachable at (LAN + Tailscale), newest
/// knowledge first — what the reconnect loop dials.</param>
/// <param name="AllowScreen">That PC may view this PC's screen without asking each time — the
/// one-time "Permitir siempre" (revocable from the PC's menu).</param>
/// <param name="AllowControl">Same idea for remote control (mouse + keyboard) while it views.</param>
public sealed record PairedPeer(string PcId, string Name, string Token, List<string> Addresses, long LastSeen = 0,
    bool AllowScreen = false, bool AllowControl = false);

/// <summary>
/// The other NotiLink PCs this one is linked to (see docs/PROTOCOL.md's "PC ↔ PC"), plus this
/// PC's own stable pcId. Encrypted with DPAPI exactly like PairedDeviceStore — the shared token
/// is the only thing that lets a peer in without a new 6-digit code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PeerStore
{
    private static readonly object Gate = new();
    private readonly string _filePath;
    private readonly string _identityPath;

    public PeerStore(string? dir = null)
    {
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink");
        _filePath = Path.Combine(dir, "paired-pcs.dat");
        _identityPath = Path.Combine(dir, "pc-id.txt");
    }

    /// <summary>Generated once and kept forever — survives renaming the PC or changing its IP.</summary>
    public string GetOrCreatePcId()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(_identityPath))
                {
                    var existing = File.ReadAllText(_identityPath).Trim();
                    if (existing.Length > 0) return existing;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_identityPath)!);
                var id = Guid.NewGuid().ToString("N");
                File.WriteAllText(_identityPath, id);
                return id;
            }
            catch (IOException)
            {
                // Can't persist it — a per-run id still works, peers just re-pair next launch.
                return Guid.NewGuid().ToString("N");
            }
        }
    }

    public IReadOnlyList<PairedPeer> List() { lock (Gate) return Load().Values.ToList(); }

    public PairedPeer? Get(string pcId) { lock (Gate) return Load().GetValueOrDefault(pcId); }

    public bool IsValidToken(string pcId, string? token) =>
        !string.IsNullOrEmpty(token) && Get(pcId)?.Token == token;

    public void Save(PairedPeer peer)
    {
        lock (Gate)
        {
            var peers = Load();
            peers[peer.PcId] = peer;
            Persist(peers);
        }
    }

    public void Update(string pcId, Func<PairedPeer, PairedPeer> change)
    {
        lock (Gate)
        {
            var peers = Load();
            if (!peers.TryGetValue(pcId, out var existing)) return;
            peers[pcId] = change(existing);
            Persist(peers);
        }
    }

    public void Remove(string pcId)
    {
        lock (Gate)
        {
            var peers = Load();
            if (peers.Remove(pcId)) Persist(peers);
        }
    }

    // Same transient-failure retry as PairedDeviceStore.Load (boot-time AV locks / DPAPI not
    // ready yet) — a hiccup must not look like "no linked PCs" and force a re-pair.
    private Dictionary<string, PairedPeer> Load()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(_filePath)) return new();
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Dictionary<string, PairedPeer>>(json) ?? new();
            }
            catch (JsonException)
            {
                return new();
            }
            catch (Exception ex) when (ex is IOException or CryptographicException)
            {
                if (attempt >= 6) return new();
                Thread.Sleep(300);
            }
        }
    }

    private void Persist(Dictionary<string, PairedPeer> peers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(peers));
        File.WriteAllBytes(_filePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }
}
