namespace NotiLink.Core.Storage;

/// <summary>
/// App icons don't change per-notification, so they're cached to disk by
/// package name and only requested from the phone once per app install.
/// </summary>
public sealed class IconCache
{
    private readonly string _directory;

    public IconCache(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NotiLink", "icons");
        Directory.CreateDirectory(_directory);
    }

    public byte[]? TryGet(string packageName)
    {
        var path = PathFor(packageName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Save(string packageName, byte[] bytes) => File.WriteAllBytes(PathFor(packageName), bytes);

    private string PathFor(string packageName) => Path.Combine(_directory, $"{Sanitize(packageName)}.png");

    private static string Sanitize(string packageName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(packageName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
