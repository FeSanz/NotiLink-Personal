using System.Text.Json;

namespace NotiLink.Core.Models;

public sealed record NotificationItem
{
    public required string Key { get; init; }
    public required string PackageName { get; init; }
    public required string AppLabel { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public string? SubText { get; init; }
    public long PostedAt { get; init; }
    public bool IsOngoing { get; init; }
    public bool IsClearable { get; init; }

    /// <summary>Mirrors Android's own Notification.EXTRA_PROGRESS/_MAX/_INDETERMINATE — the
    /// standard API that drives a native progress bar in the phone's own shade (Play Store
    /// installs, Chrome downloads, WhatsApp sending a video, ...). HasProgress is true only when
    /// the source app actually called setProgress() for this notification; Progress/ProgressMax
    /// are meaningless (0) otherwise.</summary>
    public bool HasProgress { get; init; }
    public int Progress { get; init; }
    public int ProgressMax { get; init; }
    /// <summary>Still preparing, no real percentage yet (e.g. Play Store's initial
    /// "Descargando..." before the size is known) — render a moving/pulsing bar, not a frozen 0%.</summary>
    public bool ProgressIndeterminate { get; init; }

    /// <summary>True only for the synthetic "file received from phone" card (see MainViewModel.
    /// HandleIncomingFile) — drives the "Abrir archivo"/"Abrir ruta" buttons, entirely PC-local
    /// (the file already lives on this PC's own disk, no round-trip to the phone needed).</summary>
    public bool HasSavedFile { get; init; }

    /// <summary>True only for the synthetic "shared text from phone" card (see MainViewModel.
    /// HandleIncomingShare) — drives an always-present "Copiar" button that copies the card's
    /// full text, not just a URL found in it the way the normal "Copiar enlace" button does
    /// (this card might not contain a URL at all, and copying is the whole point of it).</summary>
    public bool IsSharedText { get; init; }

    /// <summary>"plain" | "bigText" | "bigPicture" | "messaging" | "inbox"</summary>
    public string Style { get; init; } = "plain";
    public string? BigText { get; init; }
    public IReadOnlyList<MessagingLine>? Messages { get; init; }
    /// <summary>A group conversation always shows a sender prefix per line (several possible
    /// senders); a 1:1 one only starts showing it once the user has actually replied — until
    /// then every line is from the one contact already identified by the notification's own
    /// header, and the prefix would be pure noise (see ParseMessages).</summary>
    public bool IsGroupConversation { get; init; }
    public IReadOnlyList<string>? InboxLines { get; init; }

    /// <summary>Mirrors Notification.color — the app's own requested accent, "#RRGGBB". Null
    /// when the app didn't request one (COLOR_DEFAULT), not a real color to render.</summary>
    public string? AccentColor { get; init; }

    /// <summary>The single bigPicture-style image only — messaging-style images live per-message on Messages.</summary>
    public bool HasImage { get; init; }
    public string? ImageTransferId { get; init; }
    public byte[]? ImageBytes { get; init; }

    /// <summary>Sender/contact profile photo (Android's notification "large icon"), shown as the
    /// primary avatar with the small app icon as a corner badge — distinct from IconBytes.</summary>
    public bool HasAvatar { get; init; }
    public string? AvatarTransferId { get; init; }
    public byte[]? AvatarBytes { get; init; }

    public byte[]? IconBytes { get; init; }

    /// <summary>The notification's own action buttons (e.g. WhatsApp's Responder/Marcar como
    /// leído/Silenciar) — index in this list is what action.invoke's actionIndex refers to.</summary>
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];

    public bool IsMessagingStyle => Style == "messaging" && Messages is { Count: > 0 };

    /// <summary>
    /// The body content to render for every non-messaging style, so the WPF
    /// template doesn't need per-style visibility switching there: inbox
    /// shows each line, bigText shows the expanded text, everything else
    /// falls back to the plain summary text. Messaging style renders from
    /// Messages directly instead, since each line can carry its own image.
    /// </summary>
    public IReadOnlyList<string> DisplayLines => Style switch
    {
        "inbox" when InboxLines is { Count: > 0 } => InboxLines,
        "bigText" when !string.IsNullOrEmpty(BigText) => [BigText],
        _ => [Text],
    };

    /// <summary>Parses a "notification.posted" payload per docs/PROTOCOL.md.</summary>
    public static NotificationItem FromPayload(JsonElement payload, byte[]? iconBytes) => new()
    {
        Key = payload.GetProperty("key").GetString() ?? "",
        PackageName = payload.GetProperty("packageName").GetString() ?? "",
        AppLabel = payload.GetProperty("appLabel").GetString() ?? "",
        Title = payload.GetProperty("title").GetString() ?? "",
        Text = payload.GetProperty("text").GetString() ?? "",
        SubText = GetOptionalString(payload, "subText"),
        PostedAt = payload.TryGetProperty("postedAt", out var ts) ? ts.GetInt64() : 0,
        IsOngoing = payload.TryGetProperty("isOngoing", out var ongoing) && ongoing.GetBoolean(),
        IsClearable = !payload.TryGetProperty("isClearable", out var clearable) || clearable.GetBoolean(),
        AccentColor = GetOptionalString(payload, "accentColor"),
        HasProgress = payload.TryGetProperty("hasProgress", out var hasProg) && hasProg.GetBoolean(),
        Progress = payload.TryGetProperty("progress", out var prog) ? prog.GetInt32() : 0,
        ProgressMax = payload.TryGetProperty("progressMax", out var progMax) ? progMax.GetInt32() : 0,
        ProgressIndeterminate = payload.TryGetProperty("progressIndeterminate", out var progInd) && progInd.GetBoolean(),
        Style = payload.TryGetProperty("style", out var style) ? style.GetString() ?? "plain" : "plain",
        BigText = GetOptionalString(payload, "bigText"),
        Messages = ParseMessages(payload, payload.TryGetProperty("isGroupConversation", out var grp0) && grp0.GetBoolean(),
            payload.GetProperty("title").GetString() ?? ""),
        IsGroupConversation = payload.TryGetProperty("isGroupConversation", out var grp) && grp.GetBoolean(),
        InboxLines = ParseInboxLines(payload),
        HasImage = payload.TryGetProperty("hasImage", out var hasImg) && hasImg.GetBoolean(),
        ImageTransferId = GetOptionalString(payload, "imageTransferId"),
        HasAvatar = payload.TryGetProperty("hasAvatar", out var hasAvatar) && hasAvatar.GetBoolean(),
        AvatarTransferId = GetOptionalString(payload, "avatarTransferId"),
        IconBytes = iconBytes,
        Actions = ParseActions(payload),
    };

    private static string? GetOptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static List<MessagingLine>? ParseMessages(JsonElement payload, bool isGroupConversation, string conversationTitle)
    {
        if (!payload.TryGetProperty("messages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        // Showing a sender label only earns its keep once there's actually more than one
        // possible sender to tell apart: always true for a group, but a 1:1 chat's messages are
        // ALL from the one contact already identified by the notification's own header — until
        // the user replies, at which point "Tú" vs. the contact's name is exactly the
        // distinction worth showing. Computed as one pass over the raw array before building
        // lines, since it depends on whether ANY message in the whole conversation is mine.
        var showSender = isGroupConversation;
        if (!showSender)
        {
            foreach (var probe in arr.EnumerateArray())
            {
                if (probe.TryGetProperty("isMe", out var probeIsMe) && probeIsMe.GetBoolean()) { showSender = true; break; }
            }
        }

        var result = new List<MessagingLine>();
        foreach (var m in arr.EnumerateArray())
        {
            var rawSender = m.TryGetProperty("sender", out var s) ? s.GetString() ?? "" : "";
            var isMe = m.TryGetProperty("isMe", out var ime) && ime.GetBoolean();
            var text = m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            // "Tú" wins over whatever literal text (if any) the app itself wrote for its own
            // message — see docs/PROTOCOL.md's isMe for why that's more reliable than trusting
            // each app's own wording. For a message that isn't mine and came through with no
            // sender at all, a 1:1 chat's single other participant can only be the conversation's
            // own title; a group's can't be guessed, so it's left blank there instead of guessing
            // wrong.
            var sender = !showSender ? "" : isMe ? "Tú" : (!string.IsNullOrEmpty(rawSender) ? rawSender : (isGroupConversation ? "" : conversationTitle));
            result.Add(new MessagingLine
            {
                Sender = sender,
                IsMe = isMe,
                Text = text,
                DisplayText = !string.IsNullOrEmpty(sender) ? $"{sender}: {text}" : text,
                Ts = m.TryGetProperty("ts", out var ts) ? ts.GetInt64() : 0,
                ImageTransferId = m.TryGetProperty("hasImage", out var hasImg) && hasImg.GetBoolean() && m.TryGetProperty("imageTransferId", out var tid)
                    ? tid.GetString()
                    : null,
                ImageMimeType = m.TryGetProperty("imageMimeType", out var mime) && mime.ValueKind == JsonValueKind.String
                    ? mime.GetString()
                    : "image/jpeg",
            });
        }
        return result;
    }

    private static List<NotificationAction> ParseActions(JsonElement payload)
    {
        if (!payload.TryGetProperty("actions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<NotificationAction>();
        foreach (var a in arr.EnumerateArray())
        {
            var replyChoices = a.TryGetProperty("replyChoices", out var rc) && rc.ValueKind == JsonValueKind.Array
                ? rc.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0).ToList()
                : [];
            result.Add(new NotificationAction
            {
                Title = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                HasReplyInput = a.TryGetProperty("hasReplyInput", out var r) && r.GetBoolean(),
                ReplyChoices = replyChoices,
            });
        }
        return result;
    }

    private static List<string>? ParseInboxLines(JsonElement payload)
    {
        if (!payload.TryGetProperty("inboxLines", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
    }
}
