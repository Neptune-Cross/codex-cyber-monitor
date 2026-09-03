using System.Text;
using CodexCyberMonitor.Domain;
using CodexCyberMonitor.Monitoring;
using CodexCyberMonitor.Parsing;

namespace CodexCyberMonitor.Infrastructure;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            TestParser();
            TestLiveMonitor();
            TestInitialPartialLineBaseline();
            TestDurableSinkRetry();
            TestPendingAlertStoreTransactions();
            TestHistoryAudit();
            Console.WriteLine("SELF_TEST_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SELF_TEST_FAILED: {exception}");
            return 1;
        }
    }

    private static void TestParser()
    {
        var fixtures = new (string Name, string Json, string? Expected)[]
        {
            (
                "用户文本不误报",
                "{\"timestamp\":\"2026-08-26T00:00:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\"codex_error_info: cyber_policy\"}}",
                null),
            (
                "Cyber 阻断",
                "{\"timestamp\":\"2026-08-26T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-block\",\"error\":{\"message\":\"redacted\",\"codex_error_info\":\"cyber_policy\"}}}",
                "CYBER_BLOCK"),
            (
                "普通完成",
                "{\"timestamp\":\"2026-08-26T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-ok\",\"error\":null}}",
                "NO_RECORDED_CYBER_POLICY"),
            (
                "其他错误",
                "{\"timestamp\":\"2026-08-26T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-other\",\"error\":{\"codex_error_info\":\"context_window_exceeded\"}}}",
                "OTHER_ERROR"),
            (
                "Cyber 改路由",
                "{\"timestamp\":\"2026-08-26T00:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"model_reroute\",\"turn_id\":\"turn-route\",\"reason\":\"high_risk_cyber_activity\"}}",
                "CYBER_REROUTE"),
            (
                "Cyber 验证",
                "{\"timestamp\":\"2026-08-26T00:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"model_verification\",\"turn_id\":\"turn-verify\",\"verifications\":[\"trusted_access_for_cyber\"]}}",
                "CYBER_VERIFICATION"),
            (
                "Cyber 等待检查",
                "{\"timestamp\":\"2026-08-26T00:00:06Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"safety_buffering\",\"turn_id\":\"turn-buffer\",\"use_cases\":[\"cyber\"],\"reasons\":[\"user_risk\"]}}",
                "CYBER_BUFFERING")
        };

        foreach (var fixture in fixtures)
        {
            var matched = CodexEventParser.TryParse(
                Encoding.UTF8.GetBytes(fixture.Json),
                "fixture.jsonl",
                "fixture.jsonl|0",
                0,
                includeNormalCompletion: true,
                out var record);
            var actual = matched ? record?.Result : null;
            if (!string.Equals(actual, fixture.Expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"解析器测试失败：{fixture.Name}，期望={fixture.Expected}，实际={actual}");
            }
        }
    }

    private static void TestLiveMonitor()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CodexCyberMonitor-Test-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(tempRoot, "sessions");
        var stateRoot = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(sessionsRoot);
        var rollout = Path.Combine(sessionsRoot, "rollout-fixture.jsonl");
        File.WriteAllText(rollout, string.Empty, new UTF8Encoding(false));

        try
        {
            var observed = new List<CodexEventRecord>();
            using var monitor = new RolloutMonitor([sessionsRoot], stateRoot);
            monitor.EventObserved += observed.Add;
            monitor.Start();

            var firstHalf =
                "{\"timestamp\":\"2026-08-26T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"partial\",\"error\":";
            File.AppendAllText(rollout, firstHalf, new UTF8Encoding(false));
            Thread.Sleep(150);
            monitor.Poll();
            if (observed.Count != 0)
            {
                throw new InvalidOperationException("半行 JSONL 被错误处理。 ");
            }

            File.AppendAllText(
                rollout,
                "null}}\n" +
                "{\"timestamp\":\"2026-08-26T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"safety_buffering\",\"turn_id\":\"buffer\",\"use_cases\":[\"cyber\"]}}\n" +
                "{\"timestamp\":\"2026-08-26T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"block\",\"error\":{\"codex_error_info\":\"cyber_policy\"}}}\n",
                new UTF8Encoding(false));

            for (var attempt = 0; attempt < 10 && observed.Count < 3; attempt++)
            {
                Thread.Sleep(150);
                monitor.Poll();
            }

            var results = observed.Select(item => item.Result).ToArray();
            var expected = new[]
            {
                "NO_RECORDED_CYBER_POLICY",
                "CYBER_BUFFERING",
                "CYBER_BLOCK"
            };

            if (!results.SequenceEqual(expected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"实时监测测试失败：{string.Join(',', results)}");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestDurableSinkRetry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CodexCyberMonitor-Durable-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(tempRoot, "sessions");
        var stateRoot = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(sessionsRoot);
        var rollout = Path.Combine(sessionsRoot, "rollout-durable.jsonl");
        File.WriteAllText(rollout, string.Empty, new UTF8Encoding(false));

        try
        {
            var pendingStore = new PendingAlertStore(stateRoot);
            var observed = new List<CodexEventRecord>();
            var sinkAttempts = 0;

            using (var monitor = new RolloutMonitor([sessionsRoot], stateRoot))
            {
                monitor.CyberEventDurableSink = record =>
                {
                    sinkAttempts++;
                    if (sinkAttempts == 1)
                    {
                        throw new IOException("模拟首次持久化失败");
                    }

                    pendingStore.Add(record);
                };
                monitor.EventObserved += observed.Add;
                monitor.Start();

                File.AppendAllText(
                    rollout,
                    "{\"timestamp\":\"2026-08-26T00:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"durable-retry\",\"error\":{\"codex_error_info\":\"cyber_policy\"}}}\n",
                    new UTF8Encoding(false));

                var firstPollHealthy = monitor.Poll();
                if (firstPollHealthy ||
                    sinkAttempts != 1 ||
                    observed.Count != 1 ||
                    pendingStore.GetPendingRecords().Count != 0)
                {
                    throw new InvalidOperationException(
                        "首次 durable sink 失败后未保留可重试状态。 ");
                }

                var prematureAcknowledgeFailed = false;
                try
                {
                    pendingStore.Acknowledge(observed);
                }
                catch (InvalidOperationException)
                {
                    prematureAcknowledgeFailed = true;
                }

                if (!prematureAcknowledgeFailed || pendingStore.GetPendingRecords().Count != 0)
                {
                    throw new InvalidOperationException(
                        "durable Add 失败期间，已派发 UI 的记录被错误确认。 ");
                }

                var secondPollHealthy = monitor.Poll();
                if (!secondPollHealthy || sinkAttempts != 2)
                {
                    throw new InvalidOperationException(
                        $"durable sink 未正确重试：attempts={sinkAttempts}");
                }

                if (pendingStore.GetPendingRecords().Count != 1 ||
                    observed.Count != 1 ||
                    monitor.CyberEvents != 1 ||
                    monitor.TotalTurns != 1)
                {
                    throw new InvalidOperationException(
                        "durable sink 重试后出现漏报或重复派发。 ");
                }

                pendingStore.Acknowledge(observed);
                if (pendingStore.GetPendingRecords().Count != 0 ||
                    new PendingAlertStore(stateRoot).GetPendingRecords().Count != 0)
                {
                    throw new InvalidOperationException(
                        "durable Add 恢复后确认未原子清除 pending 记录。 ");
                }
            }

            var replayedAfterRestart = 0;
            using (var restarted = new RolloutMonitor([sessionsRoot], stateRoot))
            {
                restarted.CyberEventDurableSink = record =>
                {
                    replayedAfterRestart++;
                    pendingStore.Add(record);
                };
                restarted.Start();
                restarted.Poll();
            }

            if (replayedAfterRestart != 0)
            {
                throw new InvalidOperationException("成功提交的 Cyber 事件在重启后被重复读取。 ");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestInitialPartialLineBaseline()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CodexCyberMonitor-Baseline-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(tempRoot, "sessions");
        var stateRoot = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(sessionsRoot);
        var rollout = Path.Combine(sessionsRoot, "rollout-baseline.jsonl");

        var historicalLine =
            "{\"timestamp\":\"2026-08-26T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"historical\",\"error\":null}}\n";
        var partialLine =
            "{\"timestamp\":\"2026-08-26T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"startup-partial\",\"error\":{\"codex_error_info\":";
        File.WriteAllText(rollout, historicalLine + partialLine, new UTF8Encoding(false));

        try
        {
            var snapshotLength = new FileInfo(rollout).Length;
            var expectedBaseline = Encoding.UTF8.GetByteCount(historicalLine);
            var actualBaseline = IncrementalJsonlReader.FindBaselineOffset(rollout, snapshotLength);
            if (actualBaseline != expectedBaseline)
            {
                throw new InvalidOperationException(
                    $"半行 baseline offset 错误：期望={expectedBaseline}，实际={actualBaseline}");
            }

            var noNewline = Path.Combine(tempRoot, "no-newline.fixture");
            File.WriteAllText(noNewline, "没有换行的首条记录", new UTF8Encoding(false));
            if (IncrementalJsonlReader.FindBaselineOffset(noNewline, new FileInfo(noNewline).Length) != 0)
            {
                throw new InvalidOperationException("无换行文件的 baseline offset 应为 0。 ");
            }

            var observed = new List<CodexEventRecord>();
            var pendingStore = new PendingAlertStore(stateRoot);
            using var monitor = new RolloutMonitor([sessionsRoot], stateRoot);
            monitor.CyberEventDurableSink = pendingStore.Add;
            monitor.EventObserved += observed.Add;
            monitor.Start();

            File.AppendAllText(rollout, "\"cyber_policy\"}}}\n", new UTF8Encoding(false));
            monitor.Poll();

            if (observed.Count != 1 ||
                observed[0].Result != "CYBER_BLOCK" ||
                observed[0].TurnId != "startup-partial" ||
                pendingStore.GetPendingRecords().Count != 1)
            {
                throw new InvalidOperationException(
                    "首次 baseline 未从正在写入的半行起点恢复，或错误回放了历史完整行。 ");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestPendingAlertStoreTransactions()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CodexCyberMonitor-Pending-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(stateRoot);

        try
        {
            var store = new PendingAlertStore(stateRoot);
            var rollbackRecord = CreateCyberRecord(0);
            var tempBlocker = Path.Combine(stateRoot, "pending-alerts.json.tmp");

            Directory.CreateDirectory(tempBlocker);
            var addFailed = false;
            try
            {
                store.Add(rollbackRecord);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                addFailed = true;
            }
            finally
            {
                Directory.Delete(tempBlocker);
            }

            if (!addFailed || store.GetPendingRecords().Count != 0)
            {
                throw new InvalidOperationException("PendingAlertStore.Add 失败后未回滚内存状态。 ");
            }

            store.Add(rollbackRecord);
            var missingRecord = CreateCyberRecord(25);
            var mixedAcknowledgeFailed = false;
            try
            {
                store.Acknowledge([rollbackRecord, missingRecord]);
            }
            catch (InvalidOperationException)
            {
                mixedAcknowledgeFailed = true;
            }

            if (!mixedAcknowledgeFailed || store.GetPendingRecords().Count != 1)
            {
                throw new InvalidOperationException(
                    "PendingAlertStore 未对整批确认先做原子存在性校验。 ");
            }

            Directory.CreateDirectory(tempBlocker);
            var acknowledgeFailed = false;
            try
            {
                store.Acknowledge([rollbackRecord]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                acknowledgeFailed = true;
            }
            finally
            {
                Directory.Delete(tempBlocker);
            }

            if (!acknowledgeFailed || store.GetPendingRecords().Count != 1)
            {
                throw new InvalidOperationException("PendingAlertStore.Acknowledge 失败后未回滚内存状态。 ");
            }
            store.Acknowledge([rollbackRecord]);

            var records = Enumerable.Range(1, 24)
                .Select(CreateCyberRecord)
                .ToArray();
            var addTasks = records
                .Select(record => Task.Run(() => store.Add(record)))
                .Concat(Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
                {
                    for (var iteration = 0; iteration < 25; iteration++)
                    {
                        _ = store.GetPendingRecords();
                    }
                })))
                .ToArray();
            Task.WaitAll(addTasks);

            var reloadedAfterAdd = new PendingAlertStore(stateRoot);
            if (store.GetPendingRecords().Count != records.Length ||
                reloadedAfterAdd.GetPendingRecords().Count != records.Length)
            {
                throw new InvalidOperationException("PendingAlertStore 并发 Add 后内存或磁盘记录不完整。 ");
            }

            var acknowledgeTasks = records
                .Select(record => Task.Run(() => store.Acknowledge([record])))
                .Concat(Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
                {
                    for (var iteration = 0; iteration < 25; iteration++)
                    {
                        _ = store.GetPendingRecords();
                    }
                })))
                .ToArray();
            Task.WaitAll(acknowledgeTasks);

            var reloadedAfterAcknowledge = new PendingAlertStore(stateRoot);
            if (store.GetPendingRecords().Count != 0 ||
                reloadedAfterAcknowledge.GetPendingRecords().Count != 0)
            {
                throw new InvalidOperationException("PendingAlertStore 并发 Acknowledge 后仍残留记录。 ");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestHistoryAudit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CodexCyberMonitor-History-{Guid.NewGuid():N}");
        var sessionsRoot = Path.Combine(tempRoot, "sessions");
        var archivedRoot = Path.Combine(tempRoot, "archived_sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(archivedRoot);

        var activeRollout = Path.Combine(sessionsRoot, "rollout-active.jsonl");
        var archivedRollout = Path.Combine(archivedRoot, "rollout-archived.jsonl");
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(
            activeRollout,
            "{\"timestamp\":\"2026-08-26T00:00:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":\"codex_error_info: cyber_policy\"}}\n" +
            "{\"timestamp\":\"2026-08-26T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-history-block\",\"error\":{\"codex_error_info\":\"cyber_policy\"}}}\n" +
            "{\"timestamp\":\"2026-08-26T00:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"incomplete\",\"error\":{\"codex_error_info\":\"cyber_policy\"}}}",
            utf8);
        File.WriteAllText(
            archivedRollout,
            "{\"timestamp\":\"2026-08-26T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-history-block\",\"error\":{\"codex_error_info\":\"cyber_policy\"}}}\n" +
            "{\"timestamp\":\"2026-08-26T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"safety_buffering\",\"turn_id\":\"turn-history-buffer\",\"use_cases\":[\"cyber\"]}}\n" +
            "{\"timestamp\":\"2026-08-26T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"model_reroute\",\"turn_id\":\"turn-history-route\",\"reason\":\"high_risk_cyber_activity\"}}\n",
            utf8);

        try
        {
            var result = HistoryAuditService.Scan([sessionsRoot, archivedRoot]);
            var expected = new[]
            {
                "CYBER_REROUTE",
                "CYBER_BUFFERING",
                "CYBER_BLOCK"
            };
            if (result.FilesScanned != 2 ||
                result.FilesFailed != 0 ||
                !result.Records.Select(record => record.Result).SequenceEqual(expected) ||
                result.Records.Select(record => record.HistoryKey).Distinct().Count() != expected.Length)
            {
                throw new InvalidOperationException(
                    $"历史审计测试失败：files={result.FilesScanned}，records={string.Join(',', result.Records.Select(record => record.Result))}");
            }

            if (result.Records.Any(record => record.TurnId == "incomplete"))
            {
                throw new InvalidOperationException("历史审计错误处理了未完成的 JSONL 半行。 ");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static CodexEventRecord CreateCyberRecord(int index)
    {
        return new CodexEventRecord(
            CodexEventKind.CyberBlock,
            DateTimeOffset.UtcNow.AddMilliseconds(index),
            $"2026-08-26T00:02:{index:00}Z",
            $"turn-{index}",
            "CYBER_BLOCK",
            "codex_error_info=cyber_policy",
            $"rollout-{index}.jsonl",
            $"fixture-{index}",
            index * 100L,
            IsCyber: true,
            IsTest: false);
    }
}
