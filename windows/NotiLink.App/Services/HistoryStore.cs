using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotiLink.App.Services;

/// <summary>
/// Persistent notification history (opt-in from Ajustes). The page sends a full snapshot of each
/// notification as it arrives/updates and a removal record (when + why) when its card goes away;
/// this keeps them in %LocalAppData%\NotiLink\history\history.json until the user deletes them —
/// nothing expires on its own.
///
/// Images arrive as data: URLs; each is written once to history\img\ (named by content hash, so
/// the same avatar/icon repeated across hundreds of entries is stored once) and the entry keeps
/// just a "hist:file" reference. The page loads them through a WebView2 virtual host
/// (<see cref="ImageHost"/>) mapped onto that folder.
/// </summary>
internal sealed partial class HistoryStore
{
    public const string ImageHost = "history.notilink.local";
    private const string RefPrefix = "hist:";

    private static readonly string[] ImageFields = { "imageDataUrl", "avatarDataUrl", "iconDataUrl" };

    private readonly string _file;
    private readonly object _gate = new();
    private readonly List<JsonObject> _entries = new();
    private readonly Dictionary<string, JsonObject> _byId = new();
    // A notification still open when the PC app last closed comes back under a NEW page-side id
    // when the phone resyncs it — it's adopted into the old entry instead of duplicated.
    private readonly Dictionary<string, string> _alias = new();
    private readonly HashSet<string> _openFromPreviousSession = new();
    private System.Threading.Timer? _saveTimer;

    public string ImageDir { get; }

    public HistoryStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink", "history");
        ImageDir = Path.Combine(dir, "img");
        Directory.CreateDirectory(ImageDir);
        _file = Path.Combine(dir, "history.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            if (JsonNode.Parse(File.ReadAllText(_file)) is not JsonArray arr) return;
            foreach (var node in arr)
            {
                if (node is not JsonObject e || e["id"]?.GetValue<string>() is not { } id) continue;
                var copy = (JsonObject)e.DeepClone();
                _entries.Add(copy);
                _byId[id] = copy;
                if (copy["removedAt"] is null) _openFromPreviousSession.Add(id);
            }
        }
        catch (Exception)
        {
            // A corrupt history file must never stop the app — it just starts empty. The broken
            // file is kept aside rather than overwritten so nothing is silently lost.
            try { File.Move(_file, _file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch (Exception) { }
            _entries.Clear();
            _byId.Clear();
        }
    }

    public void Upsert(JsonObject entry)
    {
        if (entry["id"]?.GetValue<string>() is not { } pageId) return;
        ExternalizeImages(entry);
        lock (_gate)
        {
            var id = Resolve(pageId);
            if (!_byId.ContainsKey(id))
            {
                var deviceId = entry["deviceId"]?.GetValue<string>();
                var key = entry["key"]?.GetValue<string>();
                var adopted = _entries.FirstOrDefault(x =>
                    x["removedAt"] is null && _openFromPreviousSession.Contains(x["id"]!.GetValue<string>()) &&
                    x["deviceId"]?.GetValue<string>() == deviceId && x["key"]?.GetValue<string>() == key);
                if (adopted != null)
                {
                    id = adopted["id"]!.GetValue<string>();
                    _alias[pageId] = id;
                    _openFromPreviousSession.Remove(id);
                }
            }

            entry["id"] = id;
            if (_byId.TryGetValue(id, out var existing))
            {
                entry["firstSeenAt"] = existing["firstSeenAt"]?.DeepClone() ?? entry["firstSeenAt"]?.DeepClone();
                entry["removedAt"] = existing["removedAt"]?.DeepClone();
                entry["removalReason"] = existing["removalReason"]?.DeepClone();
                _entries[_entries.IndexOf(existing)] = entry;
            }
            else
            {
                _entries.Add(entry);
            }
            _byId[id] = entry;
        }
        ScheduleSave();
    }

    public void MarkRemoved(string pageId, string reason, long at)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(Resolve(pageId), out var e) || e["removedAt"] is not null) return;
            e["removedAt"] = at;
            e["removalReason"] = reason;
        }
        ScheduleSave();
    }

    public long Delete(string id)
    {
        var before = TotalBytes();
        lock (_gate)
        {
            id = Resolve(id);
            if (!_byId.Remove(id, out var e)) return 0;
            _entries.Remove(e);
            _openFromPreviousSession.Remove(id);
        }
        SaveNow();
        CollectUnusedImages();
        return Math.Max(0, before - TotalBytes());
    }

    /// <summary>Deletes every entry first seen before [cutoffMs] (Unix ms). Returns (count, bytes freed).</summary>
    public (int Count, long Freed) DeleteOlderThan(long cutoffMs)
    {
        var before = TotalBytes();
        int count;
        lock (_gate)
        {
            var old = _entries.Where(e => (e["firstSeenAt"]?.GetValue<long>() ?? 0) < cutoffMs).ToList();
            foreach (var e in old)
            {
                var id = e["id"]!.GetValue<string>();
                _entries.Remove(e);
                _byId.Remove(id);
                _openFromPreviousSession.Remove(id);
            }
            count = old.Count;
        }
        if (count == 0) return (0, 0);
        SaveNow();
        CollectUnusedImages();
        return (count, Math.Max(0, before - TotalBytes()));
    }

    public (int Count, long Freed) Clear()
    {
        var before = TotalBytes();
        int count;
        lock (_gate)
        {
            count = _entries.Count;
            _entries.Clear();
            _byId.Clear();
            _alias.Clear();
            _openFromPreviousSession.Clear();
        }
        SaveNow();
        CollectUnusedImages();
        return (count, Math.Max(0, before - TotalBytes()));
    }

    /// <summary>Disk used by the whole history (the JSON file plus every saved image).</summary>
    public long TotalBytes()
    {
        long total = 0;
        try
        {
            if (File.Exists(_file)) total += new FileInfo(_file).Length;
            foreach (var f in Directory.EnumerateFiles(ImageDir)) total += new FileInfo(f).Length;
        }
        catch (Exception) { }
        return total;
    }

    /// <summary>{ totalBytes, count, todayCount, todayBytes } for the panel's header. Today's
    /// size counts its entries' own data plus the images they use (an image shared with older
    /// entries, like an app icon, is counted here too — so it's "what today's entries use").</summary>
    public string StatsJson()
    {
        var todayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        int count, todayCount = 0;
        long todayBytes = 0;
        var todayImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            count = _entries.Count;
            foreach (var e in _entries)
            {
                if ((e["firstSeenAt"]?.GetValue<long>() ?? 0) < todayStart) continue;
                todayCount++;
                todayBytes += Encoding.UTF8.GetByteCount(e.ToJsonString());
                foreach (var slot in EnumerateImageSlots(e))
                    if (slot.Get() is { } v && v.StartsWith(RefPrefix)) todayImages.Add(v[RefPrefix.Length..]);
            }
        }
        foreach (var name in todayImages)
        {
            try { var f = Path.Combine(ImageDir, name); if (File.Exists(f)) todayBytes += new FileInfo(f).Length; } catch (Exception) { }
        }
        return $"{{\"totalBytes\":{TotalBytes()},\"count\":{count},\"todayCount\":{todayCount},\"todayBytes\":{todayBytes}}}";
    }

    /// <summary>All entries as a JSON array for the page, image references turned into URLs the
    /// page can load. An entry left open by a previous session and never seen again is reported
    /// with reason "unknown" — it went away while the PC app wasn't running.</summary>
    public string ListJson()
    {
        var sb = new StringBuilder("[");
        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = (JsonObject)_entries[i].DeepClone();
                if (e["removedAt"] is null && _openFromPreviousSession.Contains(e["id"]!.GetValue<string>()))
                    e["removalReason"] = "unknown";
                foreach (var img in EnumerateImageSlots(e))
                {
                    if (img.Get() is { } v && v.StartsWith(RefPrefix)) img.Set($"https://{ImageHost}/{v[RefPrefix.Length..]}");
                }
                if (i > 0) sb.Append(',');
                sb.Append(e.ToJsonString());
            }
        }
        return sb.Append(']').ToString();
    }

    private string Resolve(string id) => _alias.TryGetValue(id, out var real) ? real : id;

    private readonly record struct ImageSlot(Func<string?> Get, Action<string?> Set);

    private static IEnumerable<ImageSlot> EnumerateImageSlots(JsonObject e)
    {
        foreach (var f in ImageFields)
        {
            var field = f;
            yield return new ImageSlot(() => e[field]?.GetValue<string>(), v => e[field] = v);
        }
        if (e["messages"] is JsonArray msgs)
        {
            foreach (var m in msgs.OfType<JsonObject>())
            {
                var msg = m;
                yield return new ImageSlot(() => msg["imageDataUrl"]?.GetValue<string>(), v => msg["imageDataUrl"] = v);
            }
        }
    }

    [GeneratedRegex(@"^data:image/([a-zA-Z0-9.+-]+);base64,(.+)$", RegexOptions.Singleline)]
    private static partial Regex DataUrlRegex();

    private void ExternalizeImages(JsonObject e)
    {
        foreach (var slot in EnumerateImageSlots(e))
        {
            var v = slot.Get();
            if (string.IsNullOrEmpty(v)) continue;
            if (v.StartsWith($"https://{ImageHost}/")) { slot.Set(RefPrefix + v[($"https://{ImageHost}/").Length..]); continue; }
            var m = DataUrlRegex().Match(v);
            if (!m.Success) { slot.Set(null); continue; }
            try
            {
                var bytes = Convert.FromBase64String(m.Groups[2].Value);
                var ext = m.Groups[1].Value.ToLowerInvariant() switch
                {
                    "jpeg" or "jpg" => "jpg",
                    "png" => "png",
                    "gif" => "gif",
                    "webp" => "webp",
                    _ => "img",
                };
                var name = Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant() + "." + ext;
                var path = Path.Combine(ImageDir, name);
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
                slot.Set(RefPrefix + name);
            }
            catch (Exception)
            {
                slot.Set(null);
            }
        }
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            _saveTimer ??= new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(1500, Timeout.Infinite);
        }
    }

    private void SaveNow()
    {
        string json;
        lock (_gate)
        {
            var sb = new StringBuilder("[");
            for (var i = 0; i < _entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_entries[i].ToJsonString());
            }
            json = sb.Append(']').ToString();
        }
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception)
        {
            // Best-effort; the next change retries.
        }
    }

    /// <summary>Flushes any pending write — called on app exit so the last changes aren't lost.</summary>
    public void Flush() => SaveNow();

    private void CollectUnusedImages()
    {
        HashSet<string> used;
        lock (_gate)
        {
            used = _entries.SelectMany(EnumerateImageSlots)
                .Select(s => s.Get())
                .Where(v => v != null && v.StartsWith(RefPrefix))
                .Select(v => v![RefPrefix.Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(ImageDir))
            {
                if (!used.Contains(Path.GetFileName(f))) { try { File.Delete(f); } catch (Exception) { } }
            }
        }
        catch (Exception)
        {
            // Leftover image files are harmless; nothing to do.
        }
    }
}
