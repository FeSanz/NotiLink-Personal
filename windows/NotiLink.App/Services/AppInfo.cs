using System.IO;
using System.Reflection;

namespace NotiLink.App.Services;

/// <summary>Version/build info for the "Acerca de" section — kept in sync by hand with
/// android/app/build.gradle.kts's versionName (see NotiLink.App.csproj's &lt;Version&gt;).
/// BuildTimestamp comes from GeneratedBuildInfo, a .cs file MSBuild writes fresh on every
/// build (NotiLink.App.csproj's GenerateBuildInfo target) — not a file timestamp, so it
/// stays correct through single-file publish where Assembly.Location is empty (see the
/// IL3000 note in MainWindow).</summary>
internal static class AppInfo
{
    public const string Author = "Brandroid";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string BuildTimestamp { get; } = GeneratedBuildInfo.BuildTimestamp;

    /// <summary>NotiLink's own app icon as PNG bytes — used as the icon for synthetic cards
    /// this app renders itself (e.g. an incoming phone->PC share) that don't come from any
    /// real Android package. Same Environment.ProcessPath trick as MainWindow's tray icon,
    /// since Assembly.Location is empty in a single-file publish (see IL3000 there).</summary>
    public static byte[]? IconBytes { get; } = LoadIconBytes();

    private static byte[]? LoadIconBytes()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
