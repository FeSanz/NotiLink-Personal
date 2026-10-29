namespace NotiLink.Core.Models;

public sealed record MessagingLine
{
    /// <summary>Canonical display name — "Tú" when IsMe, otherwise the app-reported sender name
    /// (falling back to the conversation's own title for a 1:1 chat if the app didn't attribute
    /// this particular message). Already normalized at parse time; never the raw wire "sender"
    /// string as-is for the local user's own messages (see NotificationItem.ParseMessages).</summary>
    public required string Sender { get; init; }
    /// <summary>True for the local device owner's own message (see docs/PROTOCOL.md's isMe) —
    /// drives both the "Tú" label and a distinct text color on the PC (see app.css's .msg-mine),
    /// the same way WhatsApp's own UI visually separates "what I sent" from "what they sent".</summary>
    public bool IsMe { get; init; }
    public required string Text { get; init; }
    /// <summary>What the card actually renders: "Sender: Text" — always prefixed now, 1:1
    /// included (previously only group chats), per user feedback that identifying who's talking
    /// matters there too, not just in a group.</summary>
    public required string DisplayText { get; init; }
    public long Ts { get; init; }
    public string? ImageTransferId { get; init; }
    /// <summary>"image/jpeg" for a downscaled static photo, or the original "image/gif"/
    /// "image/webp" when it's an animated sticker forwarded byte-for-byte (see docs/PROTOCOL.md).</summary>
    public string? ImageMimeType { get; init; }
    public byte[]? ImageBytes { get; init; }
}
