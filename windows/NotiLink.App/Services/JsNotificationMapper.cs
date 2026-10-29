using System.Text.Json;
using System.Text.Json.Serialization;
using NotiLink.App.ViewModels;
using NotiLink.Core.Models;

namespace NotiLink.App.Services;

/// <summary>Maps a Core NotificationItem to the plain JSON shape wwwroot/app.js expects.</summary>
internal static class JsNotificationMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToJson(NotificationItem item)
    {
        var dto = new JsNotification(
            Key: item.Key,
            PackageName: item.PackageName,
            AppLabel: item.AppLabel,
            Title: item.Title,
            Style: item.Style,
            IsMessaging: item.IsMessagingStyle,
            DisplayLines: item.DisplayLines,
            Messages: item.Messages?.Select(m => new JsMessage(m.Sender, m.IsMe, m.Text, m.DisplayText, m.ImageTransferId, null)).ToList()
                      ?? new List<JsMessage>(),
            ImageDataUrl: null,
            IconDataUrl: ToDataUrl(item.IconBytes, "image/png"),
            AvatarDataUrl: null,
            IsClearable: item.IsClearable,
            PostedAt: item.PostedAt,
            Actions: item.Actions.Select(a => new JsAction(a.Title, a.HasReplyInput, a.ReplyChoices)).ToList(),
            HasSavedFile: item.HasSavedFile,
            IsSharedText: item.IsSharedText,
            HasProgress: item.HasProgress,
            Progress: item.Progress,
            ProgressMax: item.ProgressMax,
            ProgressIndeterminate: item.ProgressIndeterminate,
            AccentColor: item.AccentColor
        );
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static string ToAppListJson(IReadOnlyList<AppFilterEntry> apps)
    {
        var dto = apps.Select(a => new JsAppEntry(a.PackageName, a.Label, a.Allowed, ToDataUrl(a.IconBytes, "image/png"), a.IsSystemApp)).ToList();
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    public static string ToDeviceListJson(IReadOnlyList<DeviceListEntry> devices) =>
        JsonSerializer.Serialize(devices, JsonOptions);

    public static string? ToDataUrl(byte[]? bytes, string mimeType) =>
        bytes == null ? null : $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

    private sealed record JsAppEntry(string PackageName, string Label, bool Allowed, string? IconDataUrl, bool IsSystemApp);

    private sealed record JsNotification(
        string Key,
        string PackageName,
        string AppLabel,
        string Title,
        string Style,
        bool IsMessaging,
        IReadOnlyList<string> DisplayLines,
        List<JsMessage> Messages,
        string? ImageDataUrl,
        string? IconDataUrl,
        string? AvatarDataUrl,
        bool IsClearable,
        long PostedAt,
        List<JsAction> Actions,
        bool HasSavedFile,
        bool IsSharedText,
        bool HasProgress,
        int Progress,
        int ProgressMax,
        bool ProgressIndeterminate,
        string? AccentColor);

    private sealed record JsMessage(string Sender, bool IsMe, string Text, string DisplayText, string? ImageTransferId, string? ImageDataUrl);

    private sealed record JsAction(string Title, bool HasReplyInput, IReadOnlyList<string> ReplyChoices);
}
