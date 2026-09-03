namespace CodexCyberMonitor.Monitoring;

internal sealed class FileCursorState
{
    public required string Path { get; init; }
    public long CreationTimeUtcTicks { get; set; }
    public long ReadOffset { get; set; }
    public long CommittedOffset { get; set; }
    public long PendingStartOffset { get; set; }
    public byte[] Pending { get; set; } = [];
    public bool SkippingOversizedLine { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public string Identity => $"{System.IO.Path.GetFileName(Path)}|{CreationTimeUtcTicks}";
}

internal sealed record CompleteJsonLine(byte[] Utf8, long Offset, long EndOffset);

internal sealed record PersistedCursor(long CreationTimeUtcTicks, long CommittedOffset);
