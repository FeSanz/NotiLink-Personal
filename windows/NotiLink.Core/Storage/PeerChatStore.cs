using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotiLink.Core.Storage;

/// <summary>One line of a PC ↔ PC conversation. Kind: "text" | "clipboard" | "file" | "nudge".
/// File items are upserted as the transfer progresses (State: "sending" | "receiving" | "done" |
/// "failed" | "canceled"); Path is set on the side that has the file on disk.</summary>
public sealed class PeerChatItem
{
    public string Id { get; set; } = "";
    public bool Outgoing { get; set; }
    public string Kind { get; set; } = "text";
    public string? Text { get; set; }
    public long At { get; set; }
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public long Transferred { get; set; }
    public string? State { get; set; }
    public string? Path { get; set; }
    /// <summary>Removed from that PC's activity cards (its tab in the device switcher) — the
    /// conversation in "PCs vinculadas" still shows it.</summary>
    public bool Dismissed { get; set; }
}

/// <summary>Plain-JSON history per linked PC (%LocalAppData%\NotiLink\pc-chat\{pcId}.json),
/// capped so it never grows without bound. Not encrypted — same as the notification history.</summary>
public sealed class PeerChatStore
{
    private const int MaxItems = 300;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;
    private readonly Dictionary<string, List<PeerChatItem>> _cache = new();

    public PeerChatStore(string? dir = null)
    {
        _dir = dir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink", "pc-chat");
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    public IReadOnlyList<PeerChatItem> List(string pcId)
    {
        lock (_cache) return Items(pcId).ToList();
    }

    /// <summary>Adds the item, or replaces the one with the same Id (file progress/state).</summary>
    public void Upsert(string pcId, PeerChatItem item, bool persist = true)
    {
        lock (_cache)
        {
            var items = Items(pcId);
            var i = items.FindIndex(x => x.Id == item.Id);
            if (i >= 0) items[i] = item; else items.Add(item);
            if (items.Count > MaxItems) items.RemoveRange(0, items.Count - MaxItems);
            if (persist) Save(pcId, items);
        }
    }

    public PeerChatItem? Find(string pcId, string id)
    {
        lock (_cache) return Items(pcId).FirstOrDefault(x => x.Id == id);
    }

    /// <summary>Marks one item ([id]) or every item ([id] null) as dismissed from the activity cards.</summary>
    public void Dismiss(string pcId, string? id)
    {
        lock (_cache)
        {
            var items = Items(pcId);
            foreach (var it in items.Where(x => id == null || x.Id == id)) it.Dismissed = true;
            Save(pcId, items);
        }
    }

    public void Clear(string pcId)
    {
        lock (_cache)
        {
            _cache[pcId] = new();
            try { File.Delete(PathFor(pcId)); } catch (IOException) { }
        }
    }

    private string PathFor(string pcId) =>
        System.IO.Path.Combine(_dir, string.Concat(pcId.Where(char.IsLetterOrDigit)) + ".json");

    private List<PeerChatItem> Items(string pcId)
    {
        if (_cache.TryGetValue(pcId, out var items)) return items;
        items = new();
        try
        {
            var path = PathFor(pcId);
            if (File.Exists(path))
                items = JsonSerializer.Deserialize<List<PeerChatItem>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        // A transfer that was mid-flight when the app closed can never finish now.
        foreach (var it in items.Where(x => x.State is "sending" or "receiving")) it.State = "failed";
        _cache[pcId] = items;
        return items;
    }

    private void Save(string pcId, List<PeerChatItem> items)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(PathFor(pcId), JsonSerializer.Serialize(items, Json));
        }
        catch (IOException) { }
    }
}
