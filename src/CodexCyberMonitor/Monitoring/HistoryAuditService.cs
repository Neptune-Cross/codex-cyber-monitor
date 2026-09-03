using CodexCyberMonitor.Domain;
using CodexCyberMonitor.Parsing;

namespace CodexCyberMonitor.Monitoring;

internal sealed record HistoryAuditResult(
    IReadOnlyList<CodexEventRecord> Records,
    int FilesScanned,
    long CandidateLines,
    int FilesFailed,
    DateTimeOffset CompletedAt);

internal static class HistoryAuditService
{
    private const int BufferSize = 64 * 1024;
    private const int MaxLineBytes = 32 * 1024 * 1024;
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    public static Task<HistoryAuditResult> ScanAsync(
        IEnumerable<string> roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var fullRoots = roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.Run(
            () => Scan(fullRoots, cancellationToken),
            cancellationToken);
    }

    internal static HistoryAuditResult Scan(
        IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        var history = new Dictionary<string, CodexEventRecord>(StringComparer.Ordinal);
        var filesScanned = 0;
        var filesFailed = 0;
        long candidateLines = 0;

        foreach (var root in roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] paths;
            try
            {
                paths = Directory.EnumerateFiles(
                        root,
                        "rollout-*.jsonl",
                        EnumerationOptions)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                filesFailed++;
                continue;
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var creationTicks = File.GetCreationTimeUtc(path).Ticks;
                    var identity = $"{Path.GetFileName(path)}|{creationTicks}";
                    foreach (var line in ReadCompleteLines(path, cancellationToken))
                    {
                        if (!IsCandidate(line.Utf8))
                        {
                            continue;
                        }

                        candidateLines++;
                        if (CodexEventParser.TryParse(
                                line.Utf8,
                                path,
                                identity,
                                line.Offset,
                                includeNormalCompletion: false,
                                out var record) &&
                            record is { IsCyber: true })
                        {
                            history.TryAdd(record.HistoryKey, record);
                        }
                    }
                    filesScanned++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    filesFailed++;
                }
            }
        }

        var records = history.Values
            .OrderByDescending(GetEventTime)
            .ThenByDescending(record => record.TurnId, StringComparer.Ordinal)
            .ToArray();
        return new HistoryAuditResult(
            records,
            filesScanned,
            candidateLines,
            filesFailed,
            DateTimeOffset.Now);
    }

    private static IEnumerable<CompleteJsonLine> ReadCompleteLines(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);
        using var lineBuffer = new MemoryStream();
        var buffer = new byte[BufferSize];
        long absoluteOffset = 0;
        long lineStartOffset = 0;
        var skippingOversizedLine = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                yield break;
            }

            var segmentStart = 0;
            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                if (!skippingOversizedLine)
                {
                    var segmentLength = index - segmentStart;
                    if (lineBuffer.Length + segmentLength <= MaxLineBytes)
                    {
                        lineBuffer.Write(buffer, segmentStart, segmentLength);
                        var line = lineBuffer.ToArray();
                        if (line.Length > 0 && line[^1] == (byte)'\r')
                        {
                            Array.Resize(ref line, line.Length - 1);
                        }
                        yield return new CompleteJsonLine(
                            line,
                            lineStartOffset,
                            absoluteOffset + index + 1);
                    }
                }

                lineBuffer.SetLength(0);
                skippingOversizedLine = false;
                lineStartOffset = absoluteOffset + index + 1;
                segmentStart = index + 1;
            }

            if (!skippingOversizedLine && segmentStart < bytesRead)
            {
                var remaining = bytesRead - segmentStart;
                if (lineBuffer.Length + remaining <= MaxLineBytes)
                {
                    lineBuffer.Write(buffer, segmentStart, remaining);
                }
                else
                {
                    lineBuffer.SetLength(0);
                    skippingOversizedLine = true;
                }
            }

            absoluteOffset += bytesRead;
        }
    }

    private static bool IsCandidate(byte[] line)
    {
        var span = line.AsSpan();
        return span.IndexOf("task_complete"u8) >= 0 ||
               span.IndexOf("model_reroute"u8) >= 0 ||
               span.IndexOf("model_verification"u8) >= 0 ||
               span.IndexOf("safety_buffering"u8) >= 0;
    }

    private static DateTimeOffset GetEventTime(CodexEventRecord record)
    {
        return DateTimeOffset.TryParse(record.SourceTimestamp, out var timestamp)
            ? timestamp
            : record.ObservedAt;
    }
}
