namespace NotiLink.Core.Models;

public sealed record AppFilterEntry
{
    public required string PackageName { get; init; }
    public required string Label { get; init; }
    public bool Allowed { get; init; } = true;
    public byte[]? IconBytes { get; init; }
    public bool IsSystemApp { get; init; }
}
