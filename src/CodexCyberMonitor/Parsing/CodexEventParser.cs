using System.Text.Json;
using CodexCyberMonitor.Domain;

namespace CodexCyberMonitor.Parsing;

internal static class CodexEventParser
{
    public static bool TryParse(
        ReadOnlyMemory<byte> utf8Json,
        string sourcePath,
        string sourceIdentity,
        long lineOffset,
        bool includeNormalCompletion,
        out CodexEventRecord? record)
    {
        record = null;

        if (utf8Json.Length >= 3 &&
            utf8Json.Span[0] == 0xEF &&
            utf8Json.Span[1] == 0xBB &&
            utf8Json.Span[2] == 0xBF)
        {
            utf8Json = utf8Json[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;

            if (!TryGetString(root, "type", out var topLevelType) || topLevelType != "event_msg")
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !TryGetString(payload, "type", out var payloadType))
            {
                return false;
            }

            TryGetString(root, "timestamp", out var sourceTimestamp);
            TryGetString(payload, "turn_id", out var turnId);
            sourceTimestamp ??= string.Empty;
            turnId ??= string.Empty;

            switch (payloadType)
            {
                case "task_complete":
                    var errorInfo = GetNestedErrorInfo(payload);
                    if (errorInfo == "cyber_policy")
                    {
                        record = Create(
                            CodexEventKind.CyberBlock,
                            sourceTimestamp,
                            turnId,
                            "CYBER_BLOCK",
                            "codex_error_info=cyber_policy",
                            sourcePath,
                            sourceIdentity,
                            lineOffset,
                            isCyber: true);
                        return true;
                    }

                    if (!includeNormalCompletion)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(errorInfo))
                    {
                        record = Create(
                            CodexEventKind.TurnCompleted,
                            sourceTimestamp,
                            turnId,
                            "NO_RECORDED_CYBER_POLICY",
                            "task_complete 未记录 cyber_policy",
                            sourcePath,
                            sourceIdentity,
                            lineOffset,
                            isCyber: false);
                    }
                    else
                    {
                        record = Create(
                            CodexEventKind.OtherError,
                            sourceTimestamp,
                            turnId,
                            "OTHER_ERROR",
                            $"codex_error_info={errorInfo}",
                            sourcePath,
                            sourceIdentity,
                            lineOffset,
                            isCyber: false);
                    }

                    return true;

                case "model_reroute":
                    if (TryGetString(payload, "reason", out var reason) &&
                        reason == "high_risk_cyber_activity")
                    {
                        record = Create(
                            CodexEventKind.CyberReroute,
                            sourceTimestamp,
                            turnId,
                            "CYBER_REROUTE",
                            "reason=high_risk_cyber_activity",
                            sourcePath,
                            sourceIdentity,
                            lineOffset,
                            isCyber: true);
                        return true;
                    }
                    break;

                case "model_verification":
                    if (ArrayContains(payload, "verifications", "trusted_access_for_cyber"))
                    {
                        record = Create(
                            CodexEventKind.CyberVerification,
                            sourceTimestamp,
                            turnId,
                            "CYBER_VERIFICATION",
                            "verification=trusted_access_for_cyber",
                            sourcePath,
                            sourceIdentity,
                            lineOffset,
                            isCyber: true);
                        return true;
                    }
                    break;

                case "safety_buffering":
                    if (ArrayContains(payload, "use_cases", "cyber"))
                    {
                        var detail = ArrayContains(payload, "reasons", "user_risk")
                            ? "use_cases=cyber; reasons=user_risk"
                            : "use_cases=cyber";

                        record = Create(
                            CodexEventKind.CyberBuffering,
                            sourceTimestamp,
                            turnId,
                            "CYBER_BUFFERING",
                            detail,
                            sourcePath,
                            sourceIdentity,
                            lineOffset,
                            isCyber: true);
                        return true;
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static CodexEventRecord Create(
        CodexEventKind kind,
        string sourceTimestamp,
        string turnId,
        string result,
        string detail,
        string sourcePath,
        string sourceIdentity,
        long lineOffset,
        bool isCyber)
    {
        return new CodexEventRecord(
            kind,
            DateTimeOffset.Now,
            sourceTimestamp,
            turnId,
            result,
            detail,
            sourcePath,
            sourceIdentity,
            lineOffset,
            isCyber);
    }

    private static string? GetNestedErrorInfo(JsonElement payload)
    {
        if (!payload.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(error, "codex_error_info", out var value) ? value : null;
    }

    private static bool ArrayContains(JsonElement parent, string propertyName, string expected)
    {
        if (!parent.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() == expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }
}
