namespace CodexCyberMonitor.Monitoring;

internal static class IncrementalJsonlReader
{
    private const int MaxReadPerPoll = 8 * 1024 * 1024;
    private const int MaxLineBytes = 32 * 1024 * 1024;
    private const int TailScanBufferBytes = 64 * 1024;

    public static long FindBaselineOffset(string path, long snapshotLength)
    {
        if (snapshotLength <= 0)
        {
            return 0;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: TailScanBufferBytes,
            options: FileOptions.RandomAccess);

        var searchLength = Math.Min(snapshotLength, stream.Length);
        if (searchLength <= 0)
        {
            return 0;
        }

        stream.Position = searchLength - 1;
        if (stream.ReadByte() == (byte)'\n')
        {
            return searchLength;
        }

        var buffer = new byte[TailScanBufferBytes];
        var searchEnd = searchLength - 1;
        while (searchEnd > 0)
        {
            var count = (int)Math.Min(buffer.Length, searchEnd);
            var start = searchEnd - count;
            stream.Position = start;

            var totalRead = 0;
            while (totalRead < count)
            {
                var read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0)
                {
                    throw new EndOfStreamException("基线扫描期间日志文件被截断。");
                }
                totalRead += read;
            }

            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return start + index + 1;
                }
            }

            searchEnd = start;
        }

        return 0;
    }

    public static IReadOnlyList<CompleteJsonLine> ReadAvailable(FileCursorState state)
    {
        var lines = new List<CompleteJsonLine>();
        using var stream = new FileStream(
            state.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);

        var creationTicks = File.GetCreationTimeUtc(state.Path).Ticks;
        if (state.CreationTimeUtcTicks != 0 && state.CreationTimeUtcTicks != creationTicks)
        {
            Reset(state, creationTicks);
        }
        else
        {
            state.CreationTimeUtcTicks = creationTicks;
        }

        if (stream.Length < state.ReadOffset)
        {
            Reset(state, creationTicks);
        }

        var available = Math.Min(stream.Length - state.ReadOffset, MaxReadPerPoll);
        if (available <= 0)
        {
            state.LastSeenUtc = DateTime.UtcNow;
            return lines;
        }

        stream.Position = state.ReadOffset;
        var previousReadOffset = state.ReadOffset;
        var newBytes = new byte[(int)available];
        var totalRead = 0;
        while (totalRead < newBytes.Length)
        {
            var read = stream.Read(newBytes, totalRead, newBytes.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead != newBytes.Length)
        {
            Array.Resize(ref newBytes, totalRead);
        }

        state.ReadOffset = previousReadOffset + totalRead;
        state.LastSeenUtc = DateTime.UtcNow;

        var newStart = 0;
        if (state.SkippingOversizedLine)
        {
            var newline = Array.IndexOf(newBytes, (byte)'\n');
            if (newline < 0)
            {
                state.PendingStartOffset = state.ReadOffset;
                return lines;
            }

            state.PendingStartOffset = previousReadOffset + newline + 1;
            state.SkippingOversizedLine = false;
            newStart = newline + 1;
        }

        var newLength = newBytes.Length - newStart;
        var combined = new byte[state.Pending.Length + newLength];
        if (state.Pending.Length > 0)
        {
            Buffer.BlockCopy(state.Pending, 0, combined, 0, state.Pending.Length);
        }
        if (newLength > 0)
        {
            Buffer.BlockCopy(newBytes, newStart, combined, state.Pending.Length, newLength);
        }

        var combinedBaseOffset = state.PendingStartOffset;
        var lineStart = 0;
        for (var index = 0; index < combined.Length; index++)
        {
            if (combined[index] != (byte)'\n')
            {
                continue;
            }

            var lineLength = index - lineStart;
            if (lineLength > 0 && combined[index - 1] == (byte)'\r')
            {
                lineLength--;
            }

            if (lineLength <= MaxLineBytes)
            {
                var line = new byte[lineLength];
                if (lineLength > 0)
                {
                    Buffer.BlockCopy(combined, lineStart, line, 0, lineLength);
                }
                lines.Add(new CompleteJsonLine(
                    line,
                    combinedBaseOffset + lineStart,
                    combinedBaseOffset + index + 1));
            }

            lineStart = index + 1;
        }

        var remaining = combined.Length - lineStart;
        if (remaining > MaxLineBytes)
        {
            state.Pending = [];
            state.PendingStartOffset = state.ReadOffset;
            state.SkippingOversizedLine = true;
        }
        else
        {
            state.Pending = new byte[remaining];
            if (remaining > 0)
            {
                Buffer.BlockCopy(combined, lineStart, state.Pending, 0, remaining);
            }
            state.PendingStartOffset = combinedBaseOffset + lineStart;
        }

        return lines;
    }

    private static void Reset(FileCursorState state, long creationTicks)
    {
        state.CreationTimeUtcTicks = creationTicks;
        state.ReadOffset = 0;
        state.CommittedOffset = 0;
        state.PendingStartOffset = 0;
        state.Pending = [];
        state.SkippingOversizedLine = false;
    }

    public static void RewindToCommitted(FileCursorState state)
    {
        state.ReadOffset = state.CommittedOffset;
        state.PendingStartOffset = state.CommittedOffset;
        state.Pending = [];
        state.SkippingOversizedLine = false;
    }
}
