using System.Collections.Concurrent;
using CodexCyberMonitor.Domain;
using CodexCyberMonitor.Parsing;

namespace CodexCyberMonitor.Monitoring;

internal sealed class RolloutMonitor : IDisposable
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    private readonly string[] _roots;
    private readonly CursorStore _cursorStore;
    private readonly Dictionary<string, FileCursorState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _observedKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _observedOrder = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private DateTime _lastCursorSaveUtc = DateTime.MinValue;
    private bool _cursorDirty;
    private bool _forceReconcile;
    private bool _faultDuringPoll;
    private bool _disposed;

    public RolloutMonitor(IEnumerable<string> roots, string stateDirectory)
    {
        _roots = roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_roots.Length == 0)
        {
            throw new ArgumentException("至少需要一个监测目录。", nameof(roots));
        }

        foreach (var root in _roots)
        {
            Directory.CreateDirectory(root);
        }
        _cursorStore = new CursorStore(stateDirectory);
    }

    public event Action<CodexEventRecord>? EventObserved;
    public event Action<string>? MonitorFault;
    public Action<CodexEventRecord>? CyberEventDurableSink { get; set; }

    public long TotalTurns { get; private set; }
    public long CyberEvents { get; private set; }
    public DateTimeOffset LastScanAt { get; private set; } = DateTimeOffset.Now;
    public int TrackedFiles => _states.Count;

    public void Start()
    {
        ThrowIfDisposed();
        DiscoverFiles(baselineExistingFiles: !_cursorStore.IsInitialized);
        SaveCursors(force: true);

        foreach (var root in _roots)
        {
            var watcher = new FileSystemWatcher(root, "rollout-*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime
            };
            watcher.Changed += OnPathChanged;
            watcher.Created += OnPathChanged;
            watcher.Renamed += OnPathRenamed;
            watcher.Error += (_, _) => _forceReconcile = true;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        _forceReconcile = true;
    }

    public bool Poll()
    {
        ThrowIfDisposed();
        _faultDuringPoll = false;

        if (_forceReconcile || DateTime.UtcNow - _lastReconcileUtc >= TimeSpan.FromSeconds(5))
        {
            DiscoverFiles(baselineExistingFiles: false);
            _forceReconcile = false;
        }

        var paths = _pendingPaths.Keys.ToArray();
        foreach (var path in paths)
        {
            _pendingPaths.TryRemove(path, out _);
            ProcessPath(path);
        }

        LastScanAt = DateTimeOffset.Now;
        SaveCursors(force: false);
        return !_faultDuringPoll;
    }

    private void DiscoverFiles(bool baselineExistingFiles)
    {
        foreach (var root in _roots)
        {
            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "rollout-*.jsonl",
                         EnumerationOptions))
            {
                if (_states.TryGetValue(path, out var existing))
                {
                    try
                    {
                        var length = new FileInfo(path).Length;
                        if (length > existing.ReadOffset || length < existing.ReadOffset)
                        {
                            QueuePath(path);
                        }
                    }
                    catch (IOException)
                    {
                        QueuePath(path);
                    }

                    continue;
                }

                try
                {
                    var file = new FileInfo(path);
                    var creationTicks = file.CreationTimeUtc.Ticks;
                    var fileLength = file.Length;
                    var identity = $"{file.Name}|{creationTicks}";
                    long startOffset;

                    var liveMatch = _states.Values.FirstOrDefault(
                        state => string.Equals(state.Identity, identity, StringComparison.Ordinal));
                    if (liveMatch is not null && liveMatch.CommittedOffset <= fileLength)
                    {
                        startOffset = liveMatch.CommittedOffset;
                    }
                    else if (_cursorStore.TryGet(identity, out var persisted) &&
                             persisted is not null &&
                             persisted.CreationTimeUtcTicks == creationTicks &&
                             persisted.CommittedOffset >= 0 &&
                             persisted.CommittedOffset <= fileLength)
                    {
                        startOffset = persisted.CommittedOffset;
                    }
                    else
                    {
                        startOffset = baselineExistingFiles
                            ? IncrementalJsonlReader.FindBaselineOffset(path, fileLength)
                            : 0;
                    }

                    var state = new FileCursorState
                    {
                        Path = path,
                        CreationTimeUtcTicks = creationTicks,
                        ReadOffset = startOffset,
                        CommittedOffset = startOffset,
                        PendingStartOffset = startOffset
                    };
                    _states[path] = state;
                    _cursorDirty = true;

                    if (fileLength > startOffset)
                    {
                        QueuePath(path);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    NotifyFault($"发现日志文件时读取失败：{Path.GetFileName(path)}；{exception.GetType().Name}");
                }
            }
        }

        _lastReconcileUtc = DateTime.UtcNow;
    }

    private void ProcessPath(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (!_states.TryGetValue(path, out var state))
        {
            DiscoverFiles(baselineExistingFiles: false);
            if (!_states.TryGetValue(path, out state))
            {
                return;
            }
        }

        try
        {
            var lines = IncrementalJsonlReader.ReadAvailable(state);
            foreach (var line in lines)
            {
                try
                {
                    CodexEventRecord? record = null;
                    if (IsCandidate(line.Utf8) &&
                        CodexEventParser.TryParse(
                            line.Utf8,
                            path,
                            state.Identity,
                            line.Offset,
                            includeNormalCompletion: true,
                            out record) &&
                        record is not null &&
                        !_seenKeys.Contains(record.EventKey))
                    {
                        if (record.IsCyber)
                        {
                            try
                            {
                                CyberEventDurableSink?.Invoke(record);
                            }
                            catch
                            {
                                ObserveOnce(record);
                                throw;
                            }
                        }

                        // Durable sink 成功后才能登记去重键。否则失败重试时会被
                        // _seenKeys 提前拦截，随后错误地提交游标并永久漏报。
                        if (Remember(record.EventKey))
                        {
                            ObserveOnce(record);
                        }
                    }

                    state.CommittedOffset = line.EndOffset;
                    _cursorDirty = true;
                    if (record is { IsCyber: true })
                    {
                        SaveCursors(force: true);
                    }
                }
                catch (Exception exception)
                {
                    IncrementalJsonlReader.RewindToCommitted(state);
                    QueuePath(path);
                    NotifyFault($"事件处理失败：{Path.GetFileName(path)}；{exception.GetType().Name}");
                    return;
                }
            }

            var fileLength = new FileInfo(path).Length;
            if (fileLength > state.ReadOffset)
            {
                QueuePath(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            IncrementalJsonlReader.RewindToCommitted(state);
            QueuePath(path);
            NotifyFault($"增量读取失败：{Path.GetFileName(path)}；{exception.GetType().Name}");
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

    private bool Remember(string key)
    {
        if (!_seenKeys.Add(key))
        {
            return false;
        }

        _seenOrder.Enqueue(key);
        while (_seenOrder.Count > 20_000)
        {
            _seenKeys.Remove(_seenOrder.Dequeue());
        }

        return true;
    }

    private void ObserveOnce(CodexEventRecord record)
    {
        if (!RememberObserved(record.EventKey))
        {
            return;
        }

        if (record.Kind is CodexEventKind.TurnCompleted or
            CodexEventKind.OtherError or
            CodexEventKind.CyberBlock)
        {
            TotalTurns++;
        }

        if (record.IsCyber)
        {
            CyberEvents++;
        }

        try
        {
            EventObserved?.Invoke(record);
        }
        catch (Exception exception)
        {
            NotifyFault($"UI 事件派发失败：{exception.GetType().Name}");
        }
    }

    private bool RememberObserved(string key)
    {
        if (!_observedKeys.Add(key))
        {
            return false;
        }

        _observedOrder.Enqueue(key);
        while (_observedOrder.Count > 20_000)
        {
            _observedKeys.Remove(_observedOrder.Dequeue());
        }

        return true;
    }

    private void QueuePath(string path)
    {
        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            _pendingPaths[path] = 0;
        }
    }

    private void OnPathChanged(object sender, FileSystemEventArgs eventArgs)
    {
        QueuePath(eventArgs.FullPath);
    }

    private void OnPathRenamed(object sender, RenamedEventArgs eventArgs)
    {
        QueuePath(eventArgs.FullPath);
    }

    private void SaveCursors(bool force)
    {
        if (!_cursorDirty && !force)
        {
            return;
        }

        if (!force && DateTime.UtcNow - _lastCursorSaveUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _cursorStore.Update(_states.Values);
        _cursorStore.Save();
        _lastCursorSaveUtc = DateTime.UtcNow;
        _cursorDirty = false;
    }

    private void NotifyFault(string message)
    {
        _faultDuringPoll = true;
        try
        {
            MonitorFault?.Invoke(message);
        }
        catch
        {
            // 监测错误上报不参与游标提交。
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        try
        {
            _cursorStore.Update(_states.Values);
            _cursorStore.Save();
        }
        catch
        {
            // 进程退出阶段不再向 UI 抛出异常。
        }
    }
}
