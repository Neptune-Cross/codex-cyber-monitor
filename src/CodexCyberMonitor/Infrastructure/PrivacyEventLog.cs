using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexCyberMonitor.Domain;

namespace CodexCyberMonitor.Infrastructure;

internal sealed class PrivacyEventLog
{
    private readonly byte[] _salt;

    public PrivacyEventLog(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        LogsDirectory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(LogsDirectory);
        _salt = LoadOrCreateSalt(Path.Combine(baseDirectory, "install-salt.bin"));
        DeleteExpiredLogs();
    }

    public string BaseDirectory { get; }
    public string LogsDirectory { get; }

    public void AppendCyberEvent(CodexEventRecord record)
    {
        var entry = new
        {
            observed_at = record.ObservedAt.ToString("o"),
            source_timestamp = record.SourceTimestamp,
            event_name = record.Result,
            event_kind = record.Kind.ToString(),
            detail_code = record.Detail,
            turn_ref = Hash(record.TurnId),
            source_file = record.SourceFileName,
            source_ref = Hash(record.SourceIdentity),
            line_offset = record.LineOffset,
            acknowledged = false,
            is_test = record.IsTest,
            app_version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        };

        Append(entry);
    }

    public void AppendAcknowledgement(IEnumerable<CodexEventRecord> records)
    {
        var items = records.Select(record => new
        {
            event_name = record.Result,
            turn_ref = Hash(record.TurnId),
            source_ref = Hash(record.SourceIdentity),
            line_offset = record.LineOffset,
            is_test = record.IsTest
        }).ToArray();

        var entry = new
        {
            observed_at = DateTimeOffset.Now.ToString("o"),
            event_name = "ACKNOWLEDGED",
            acknowledged = true,
            events = items,
            app_version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        };

        Append(entry);
    }

    public void AppendMonitorError(string message)
    {
        var entry = new
        {
            observed_at = DateTimeOffset.Now.ToString("o"),
            event_name = "MONITOR_ERROR",
            detail_code = "monitor_error",
            error_ref = Hash(message),
            app_version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        };

        Append(entry);
    }

    private void Append<T>(T entry)
    {
        var path = Path.Combine(LogsDirectory, $"events-{DateTime.Now:yyyyMMdd}.jsonl");
        var json = JsonSerializer.Serialize(entry);
        File.AppendAllText(
            path,
            json + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string Hash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        using var hmac = new HMACSHA256(_salt);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..16].ToLowerInvariant();
    }

    private static byte[] LoadOrCreateSalt(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var salt = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, salt);
        return salt;
    }

    private void DeleteExpiredLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        foreach (var path in Directory.EnumerateFiles(LogsDirectory, "events-*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 下次启动时再次清理。
            }
        }
    }
}
