using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotiLink.Core.Storage;

/// <param name="BadgeText">The 1–2 letters/digits the user chose to tag this phone with (null = automatic).</param>
/// <param name="BadgeColor">Its "#RRGGBB" color (null = automatic).</param>
public sealed record PairedDevice(string DeviceId, string DeviceName, string Token, string? DeviceModel = null,
    string? BadgeText = null, string? BadgeColor = null);

/// <summary>
/// Persists deviceId -> token -> deviceName for every phone that has completed the
/// 6-digit pairing flow (see docs/PROTOCOL.md handshake.hello/pair.code/pair.token).
/// Encrypted at rest with Windows DPAPI, scoped to the current Windows user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PairedDeviceStore
{
    private readonly string _filePath;

    // Confirmed real bug, from a live PC-side log: "tokenProvided=True storedEntryExists=True
    // tokenMatches=False" — the store DID have an entry for that exact deviceId, it just didn't
    // match the token the phone sent, despite the phone never having been un-paired. Every public
    // method here used to do its own unsynchronized Load() -> mutate -> Persist() sequence, and
    // TcpServer handles every connected phone on its own independent, concurrent Task
    // (AcceptLoopAsync's `_ = HandleClientAsync(...)`) — with two paired phones (confirmed: this
    // user has two), both reconnecting around the same moment (both on the same home WiFi
    // recovering from one router blip, say) means both phones' HandleHandshake/HandlePairConfirm
    // calls can run this store's read-modify-write concurrently on different threads. Whichever
    // Persist() call landed last would silently overwrite the OTHER phone's just-saved token
    // with a stale in-memory snapshot taken before it existed — exactly what a token mismatch on
    // an already-paired device looks like. A single lock around every method's entire operation
    // (not just Load or just Persist individually) makes the whole read-modify-write atomic
    // relative to every other call, so this can no longer interleave.
    private static readonly object Gate = new();

    public PairedDeviceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NotiLink", "paired-devices.dat");
    }

    public bool IsValidToken(string deviceId, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (Gate)
        {
            var devices = Load();
            return devices.TryGetValue(deviceId, out var device) && device.Token == token;
        }
    }

    public void Save(PairedDevice device)
    {
        lock (Gate)
        {
            var devices = Load();
            devices[device.DeviceId] = device;
            Persist(devices);
        }
    }

    public IReadOnlyList<PairedDevice> List()
    {
        lock (Gate) return Load().Values.ToList();
    }

    public PairedDevice? Get(string deviceId)
    {
        lock (Gate) return Load().GetValueOrDefault(deviceId);
    }

    /// <summary>Finds a previously paired device with the same hardware fingerprint under a
    /// different deviceId — used to recognize "this is probably the same physical phone that
    /// re-paired after losing its old identity" (e.g. an uninstall/reinstall) so its custom
    /// name can be carried over instead of reset to the phone's generic default.</summary>
    public PairedDevice? FindByDeviceModel(string? deviceModel, string excludeDeviceId)
    {
        if (string.IsNullOrEmpty(deviceModel)) return null;
        lock (Gate) return Load().Values.FirstOrDefault(d => d.DeviceId != excludeDeviceId && d.DeviceModel == deviceModel);
    }

    /// <summary>User-editable display name — the phone's own model name (from pairing)
    /// is just the starting default, not fixed.</summary>
    public void Rename(string deviceId, string name)
    {
        lock (Gate)
        {
            var devices = Load();
            if (!devices.TryGetValue(deviceId, out var existing)) return;
            devices[deviceId] = existing with { DeviceName = name };
            Persist(devices);
        }
    }

    /// <summary>The phone's identifier (letter/number + color) — see the app's DeviceBadge.
    /// Nulls mean "automatic".</summary>
    public void SetBadge(string deviceId, string? text, string? color)
    {
        lock (Gate)
        {
            var devices = Load();
            if (!devices.TryGetValue(deviceId, out var existing)) return;
            devices[deviceId] = existing with { BadgeText = text, BadgeColor = color };
            Persist(devices);
        }
    }

    public void Revoke(string deviceId)
    {
        lock (Gate)
        {
            var devices = Load();
            if (devices.Remove(deviceId)) Persist(devices);
        }
    }

    // Retried a few times before giving up: a phone reconnecting within the first second or two
    // after Windows boots (this app auto-starts from the Startup folder — see publish.ps1) can
    // race genuinely transient conditions that have nothing to do with the data itself being
    // bad — the file momentarily locked by an antivirus real-time scan, or DPAPI's own user
    // master key not fully available yet under heavy boot-time disk/CPU contention. Without this,
    // a single such hiccup made IsValidToken silently see "no pairings at all" for that one
    // request and fall back to the 6-digit code flow — confirmed as a real, repeatable complaint
    // ("cada vez que reinicio la PC me pide el código otra vez") despite the phone never having
    // actually been un-paired. A JsonException (successfully decrypted but not valid JSON) is
    // NOT retried — that's genuinely corrupt data, not a timing issue, and retrying it would just
    // waste time before reaching the same unavoidable "start fresh" outcome.
    private const int LoadRetryAttempts = 6;
    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromMilliseconds(300);

    private Dictionary<string, PairedDevice> Load()
    {
        for (var attempt = 1; attempt <= LoadRetryAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(_filePath)) return new();
                var encrypted = File.ReadAllBytes(_filePath);
                var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Dictionary<string, PairedDevice>>(json) ?? new();
            }
            catch (JsonException)
            {
                return new();
            }
            catch (Exception ex) when (ex is IOException or CryptographicException)
            {
                if (attempt == LoadRetryAttempts)
                {
                    Debug.WriteLine($"PairedDeviceStore.Load: giving up after {LoadRetryAttempts} attempts: {ex}");
                    return new();
                }
                Thread.Sleep(LoadRetryDelay);
            }
        }
        return new(); // unreachable, but the compiler can't see the loop always returns or retries
    }

    private void Persist(Dictionary<string, PairedDevice> devices)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(devices));
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, encrypted);
    }
}
