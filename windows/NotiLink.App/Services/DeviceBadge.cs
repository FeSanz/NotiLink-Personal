using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace NotiLink.App.Services;

/// <summary>
/// The little "who is this" tag of each phone: one or two letters/digits on a colored circle,
/// shown before the device name in the app and on the taskbar icon of every window shared from it
/// (see WindowIdentity.WithDeviceBadge). The user can pick both from the device menu
/// ("Identificador"); until they do it's the name's first letter and a color derived from the id.
/// Overrides are kept here (fed from PairedDeviceStore by MainViewModel) so ScreenShareService can
/// resolve them without a reference to the store.
/// </summary>
internal static class DeviceBadge
{
    public static readonly string[] Palette =
    {
        "#7C5CFF", "#E53935", "#2E7D32", "#F57C00", "#1565C0", "#8E24AA",
        "#00897B", "#D81B60", "#6D4C41", "#455A64",
    };

    private static readonly ConcurrentDictionary<string, (string? Text, string? Color)> Overrides = new();

    public static void Set(string deviceId, string? text, string? color) => Overrides[deviceId] = (text, color);

    /// <summary>Accepts 1–2 letters/digits (uppercased); anything else means "automatic".</summary>
    public static string? NormalizeText(string? text)
    {
        var t = new string((text ?? "").Trim().Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        return t.Length == 0 ? null : t;
    }

    public static string? NormalizeColor(string? color)
    {
        var c = (color ?? "").Trim();
        return Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$") ? c.ToUpperInvariant() : null;
    }

    public static (string Text, string Color, bool Custom) Resolve(string deviceId, string deviceName)
    {
        Overrides.TryGetValue(deviceId, out var o);
        return (o.Text ?? DefaultText(deviceName), o.Color ?? DefaultColor(deviceId), o.Text != null || o.Color != null);
    }

    // "Brando's S26 Ultra" → "S": skip a leading owner's possessive, it's the same on every phone.
    private static string DefaultText(string deviceName)
    {
        var name = Regex.Replace(deviceName.Trim(), @"^\S+['’]s\s+", "");
        var letter = name.FirstOrDefault(char.IsLetterOrDigit);
        return letter == default ? "?" : char.ToUpperInvariant(letter).ToString();
    }

    private static string DefaultColor(string deviceId)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in deviceId) h = h * 31 + c;
            return Palette[(int)((uint)h % 6)]; // first six: the colors used before this was editable
        }
    }
}
