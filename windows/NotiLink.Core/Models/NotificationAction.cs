namespace NotiLink.Core.Models;

/// <summary>The notification's own real Notification.Action, verbatim — not invented.</summary>
public sealed record NotificationAction
{
    public required string Title { get; init; }
    public bool HasReplyInput { get; init; }
    /// <summary>Mirrors RemoteInput.getChoices() — canned quick-reply suggestions some apps offer
    /// alongside free-text typing (e.g. Gmail's "Sí"/"No"/"Tal vez"). Empty when HasReplyInput is
    /// false or the app didn't provide any — never null, so callers can iterate unconditionally.</summary>
    public IReadOnlyList<string> ReplyChoices { get; init; } = [];
}
