namespace NotiLink.Core.Models;

/// <summary>The phone's current "now playing" session (Spotify, YouTube Music, ...), mirrored
/// from Android's MediaSessionManager — see docs/PROTOCOL.md's media.state. Real transport
/// controls only (CanPlayPause/CanNext/CanPrevious come from the session's own declared
/// PlaybackState actions), never invented.</summary>
public sealed record MediaPlaybackState
{
    public required string PackageName { get; init; }
    public required string AppLabel { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public bool IsPlaying { get; init; }
    public long Position { get; init; }
    public long Duration { get; init; }
    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanFavorite { get; init; }
    public bool IsFavorite { get; init; }
    public bool CanShuffle { get; init; }
    public bool IsShuffleOn { get; init; }
    public bool IsSmartShuffle { get; init; }
    public bool CanSeek { get; init; }
    public byte[]? AlbumArtBytes { get; init; }
}
