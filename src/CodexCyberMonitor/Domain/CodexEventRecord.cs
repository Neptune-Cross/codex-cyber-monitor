namespace CodexCyberMonitor.Domain;

internal enum CodexEventKind
{
    TurnCompleted,
    OtherError,
    CyberBlock,
    CyberReroute,
    CyberVerification,
    CyberBuffering,
    TestWarning
}

internal sealed record CodexEventRecord(
    CodexEventKind Kind,
    DateTimeOffset ObservedAt,
    string SourceTimestamp,
    string TurnId,
    string Result,
    string Detail,
    string SourcePath,
    string SourceIdentity,
    long LineOffset,
    bool IsCyber,
    bool IsTest = false)
{
    public string SourceFileName => Path.GetFileName(SourcePath);

    public string ShortTurnId
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TurnId))
            {
                return "—";
            }

            return TurnId.Length <= 18
                ? TurnId
                : $"{TurnId[..8]}…{TurnId[^6..]}";
        }
    }

    public string EventKey => $"{SourceIdentity}|{LineOffset}|{Kind}";

    public string HistoryKey => string.IsNullOrWhiteSpace(TurnId)
        ? EventKey
        : $"{Kind}|{TurnId}";
}
