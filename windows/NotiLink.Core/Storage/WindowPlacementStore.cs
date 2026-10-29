using System.Text.Json;

namespace NotiLink.Core.Storage;

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>Persists the window's last position/size/maximized-state across app restarts.
/// Kept WPF-type-free (plain doubles) so Core has no UI framework dependency.</summary>
public sealed class WindowPlacementStore
{
    private readonly string _filePath;

    public WindowPlacementStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NotiLink", "window-placement.json");
    }

    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_filePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(placement));
    }
}
