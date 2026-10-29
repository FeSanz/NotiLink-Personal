using System.IO;

namespace NotiLink.App.Services;

/// <summary>A minimal rolling diagnostic log for the PC side — mirrors the phone's own DiagLog
/// (android/.../net/DiagLog.kt) conceptually, but this side had NO persistent logging at all
/// until now: TcpServer.Log existed as an event but nothing ever subscribed to it, so every
/// PC-side decision (why did HandleHandshake show the pairing code again? why did a device get
/// marked stale?) was completely invisible after the fact. Written to the same
/// %LocalAppData%\NotiLink\logs\ folder the phone's own synced logs already live in and the
/// "Abrir carpeta de logs" button already opens, as pc.log.</summary>
internal static class PcDiagLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotiLink", "logs", "pc.log");
    private const long MaxBytes = 500_000; // trimmed from the oldest entries once exceeded, same as the phone's own DiagLog
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        lock (Lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
                TrimIfNeeded();
            }
            catch (Exception)
            {
                // Logging must never be the thing that crashes the app.
            }
        }
    }

    private static void TrimIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var lines = File.ReadAllLines(FilePath);
        File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
    }
}
