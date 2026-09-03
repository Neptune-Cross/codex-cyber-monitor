using System.Diagnostics;
using System.Media;
using CodexCyberMonitor.Domain;
using CodexCyberMonitor.Infrastructure;
using CodexCyberMonitor.Monitoring;
using CodexCyberMonitor.UI;
using Microsoft.Win32;

namespace CodexCyberMonitor.App;

internal sealed class CyberMonitorApplicationContext : ApplicationContext
{
    private readonly string _sessionsRoot;
    private readonly string _archivedSessionsRoot;
    private readonly PrivacyEventLog _privacyLog;
    private readonly PendingAlertStore _pendingAlerts;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly RolloutMonitor _monitor;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _normalIcon;
    private readonly Icon _alertIcon;
    private readonly Icon _errorIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly Control _uiInvoker;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly ToolStripMenuItem _trayStatusItem;
    private readonly ToolStripMenuItem _showWarningItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly HashSet<string> _displayedCyberEventKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _displayedCyberEventOrder = new();
    private readonly List<CodexEventRecord> _deferredWarningRecords = [];
    private readonly Dictionary<string, CodexEventRecord> _historyRecords = new(StringComparer.Ordinal);
    private StatusForm? _statusForm;
    private WarningForm? _warningForm;
    private WarningForm? _testWarningForm;
    private System.Windows.Forms.Timer? _startupTimer;
    private Task<bool>? _pollTask;
    private Task<HistoryAuditResult>? _historyScanTask;
    private CancellationTokenSource? _historyScanCancellation;
    private HistoryAuditResult? _lastHistoryResult;
    private int _historyScanGeneration;
    private int _pollInFlight;
    private readonly List<CodexEventRecord> _recentCyberEvents = [];
    private string? _monitorFaultError;
    private string? _pendingStoreError;
    private string? _privacyLogError;
    private string? _unhandledError;
    private bool _hasPendingRealAlerts;
    private bool _pendingRefocus;
    private bool _suspendWarningMaintenance;
    private bool _exiting;
    private bool _updatingStartupItem;

    public CyberMonitorApplicationContext(string[] args)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appDataDirectory = Path.Combine(localData, "CodexCyberMonitor");
        var stateDirectory = Path.Combine(appDataDirectory, "state");
        _sessionsRoot = Path.Combine(userProfile, ".codex", "sessions");
        _archivedSessionsRoot = Path.Combine(userProfile, ".codex", "archived_sessions");

        Directory.CreateDirectory(appDataDirectory);
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(_sessionsRoot);
        Directory.CreateDirectory(_archivedSessionsRoot);

        _privacyLog = new PrivacyEventLog(appDataDirectory);
        _pendingAlerts = new PendingAlertStore(stateDirectory);
        _startupRegistration = new StartupRegistrationService(Application.ExecutablePath);
        _normalIcon = IconFactory.Create(alert: false);
        _alertIcon = IconFactory.Create(alert: true);
        _errorIcon = IconFactory.CreateError();
        _uiInvoker = new Control();
        _uiInvoker.CreateControl();

        _trayMenu = new ContextMenuStrip();
        var titleItem = new ToolStripMenuItem("Codex Cyber 实时监测器")
        {
            Enabled = false,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point)
        };
        _trayStatusItem = new ToolStripMenuItem("● 正在实时监测")
        {
            Enabled = false,
            ForeColor = Color.FromArgb(16, 124, 16)
        };
        var openStatusItem = new ToolStripMenuItem("打开监测面板");
        openStatusItem.Click += (_, _) => ShowStatusForm();
        var openHistoryItem = new ToolStripMenuItem("查看全部历史 Cyber 记录");
        openHistoryItem.Click += (_, _) => ShowStatusForm();
        _showWarningItem = new ToolStripMenuItem("显示当前红色警告")
        {
            Enabled = false
        };
        _showWarningItem.Click += (_, _) => _warningForm?.ShowPersistent();
        var testItem = new ToolStripMenuItem("测试红色警告窗");
        testItem.Click += (_, _) => ShowTestWarning();
        var openLogsItem = new ToolStripMenuItem("打开隐私日志目录");
        openLogsItem.Click += (_, _) => OpenLogsDirectory();
        _startupItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = _startupRegistration.IsEnabled
        };
        _startupItem.CheckedChanged += (_, _) => UpdateStartupRegistration();
        var exitItem = new ToolStripMenuItem("退出监测器…");
        exitItem.Click += (_, _) => ConfirmAndExit();

        _trayMenu.Items.Add(titleItem);
        _trayMenu.Items.Add(_trayStatusItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(openStatusItem);
        _trayMenu.Items.Add(openHistoryItem);
        _trayMenu.Items.Add(_showWarningItem);
        _trayMenu.Items.Add(testItem);
        _trayMenu.Items.Add(openLogsItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(_startupItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _normalIcon,
            Text = "Codex Cyber 监测器｜运行中",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                if (_warningForm is { IsDisposed: false })
                {
                    _warningForm.ShowPersistent();
                }
                else
                {
                    ShowStatusForm();
                }
            }
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatusForm();

        _monitor = new RolloutMonitor([_sessionsRoot, _archivedSessionsRoot], stateDirectory);
        _monitor.CyberEventDurableSink = _pendingAlerts.Add;
        _monitor.EventObserved += record => PostToUi(() => OnEventObserved(record));
        _monitor.MonitorFault += message => PostToUi(() => OnMonitorFault(message));
        _monitor.Start();
        _notifyIcon.Visible = true;
        StartHistoryScan(force: false);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _pollTimer.Tick += (_, _) =>
        {
            StartBackgroundPoll();
            if (!_suspendWarningMaintenance)
            {
                _warningForm?.MaintainPersistentVisibility();
                _testWarningForm?.MaintainPersistentVisibility();
            }
            if (!_suspendWarningMaintenance &&
                _pendingRefocus &&
                _warningForm is { IsDisposed: false })
            {
                _pendingRefocus = false;
                _warningForm.ShowPersistent();
            }
        };
        _pollTimer.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;

        var showAtStartup = args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        var testAlertAtStartup = args.Contains("--test-alert", StringComparer.OrdinalIgnoreCase);
        var restoredAlerts = _pendingAlerts.GetPendingRecords();
        if (showAtStartup || testAlertAtStartup || restoredAlerts.Count > 0)
        {
            _startupTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _startupTimer.Tick += (_, _) =>
            {
                _startupTimer?.Stop();
                _startupTimer?.Dispose();
                _startupTimer = null;
                if (restoredAlerts.Count > 0)
                {
                    _hasPendingRealAlerts = true;
                    foreach (var record in restoredAlerts)
                    {
                        if (RegisterRecentCyberEvent(record))
                        {
                            RegisterHistoryRecord(record);
                            ShowOrUpdateWarning(record);
                        }
                    }
                    SetAlertTrayState();
                    SystemSounds.Hand.Play();
                }
                if (showAtStartup)
                {
                    ShowStatusForm();
                }
                if (testAlertAtStartup)
                {
                    ShowTestWarning();
                }
            };
            _startupTimer.Start();
        }
    }

    public void ReportUnhandledException(Exception exception)
    {
        if (_uiInvoker.InvokeRequired)
        {
            PostToUi(() => ReportUnhandledException(exception));
            return;
        }

        var message = $"程序异常：{exception.GetType().Name}: {exception.Message}";
        try
        {
            _privacyLog.AppendMonitorError(message);
        }
        catch
        {
            // 防止异常记录失败再次触发未处理异常。
        }

        _unhandledError = message;
        _statusForm?.ReportFault(message);
        RenderTrayState();
    }

    public void HandleActivationCommand(InstanceActivationCommand command)
    {
        if (_exiting || command == InstanceActivationCommand.None)
        {
            return;
        }

        if (_uiInvoker.InvokeRequired)
        {
            PostToUi(() => HandleActivationCommand(command));
            return;
        }

        if ((command & InstanceActivationCommand.Show) != 0)
        {
            ShowStatusForm();
        }

        if ((command & InstanceActivationCommand.TestAlert) != 0)
        {
            ShowTestWarning();
        }
    }

    private void StartBackgroundPoll()
    {
        if (_exiting || Interlocked.Exchange(ref _pollInFlight, 1) != 0)
        {
            return;
        }

        _pollTask = Task.Run(() => _monitor.Poll());
        _ = _pollTask.ContinueWith(
            task => PostToUi(() =>
            {
                Interlocked.Exchange(ref _pollInFlight, 0);
                if (task.IsFaulted)
                {
                    ReportUnhandledException(task.Exception?.GetBaseException() ?? new Exception("后台监测失败。"));
                    return;
                }

                if (task.IsCanceled)
                {
                    return;
                }

                if (task.Result)
                {
                    _monitorFaultError = null;
                    RefreshStatusAndTray();
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void StartHistoryScan(bool force)
    {
        if (_exiting)
        {
            return;
        }

        if (_historyScanTask is { IsCompleted: false })
        {
            if (!force)
            {
                return;
            }
            _historyScanCancellation?.Cancel();
        }

        _historyScanCancellation?.Dispose();
        _statusForm?.SetHistoryLoading();
        var generation = ++_historyScanGeneration;
        var cancellation = new CancellationTokenSource();
        _historyScanCancellation = cancellation;
        var task = HistoryAuditService.ScanAsync(
            [_sessionsRoot, _archivedSessionsRoot],
            cancellation.Token);
        _historyScanTask = task;
        _ = task.ContinueWith(
            completed => PostToUi(() => CompleteHistoryScan(completed, generation)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void CompleteHistoryScan(Task<HistoryAuditResult> task, int generation)
    {
        if (_exiting || generation != _historyScanGeneration)
        {
            return;
        }

        _historyScanCancellation?.Dispose();
        _historyScanCancellation = null;

        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            var exception = task.Exception?.GetBaseException() ?? new Exception("历史扫描失败。 ");
            _statusForm?.ReportHistoryFault($"历史扫描失败：{exception.GetType().Name}");
            return;
        }

        var result = task.Result;
        _historyRecords.Clear();
        foreach (var record in result.Records)
        {
            _historyRecords[record.HistoryKey] = record;
        }
        foreach (var record in _recentCyberEvents.Where(record => !record.IsTest))
        {
            _historyRecords[record.HistoryKey] = record;
        }
        foreach (var record in _pendingAlerts.GetPendingRecords())
        {
            _historyRecords[record.HistoryKey] = record;
        }

        var mergedRecords = _historyRecords.Values
            .OrderByDescending(GetHistoryEventTime)
            .ThenByDescending(record => record.TurnId, StringComparer.Ordinal)
            .ToArray();
        _lastHistoryResult = result with { Records = mergedRecords };
        ApplyHistoryToStatusForm();
    }

    private void ApplyHistoryToStatusForm()
    {
        if (_statusForm is null || _statusForm.IsDisposed || _lastHistoryResult is null)
        {
            return;
        }

        var pendingKeys = _pendingAlerts.GetPendingRecords()
            .Select(record => record.HistoryKey)
            .ToHashSet(StringComparer.Ordinal);
        _statusForm.SetHistoryRecords(
            _lastHistoryResult.Records,
            pendingKeys,
            _lastHistoryResult);
    }

    private static DateTimeOffset GetHistoryEventTime(CodexEventRecord record)
    {
        return DateTimeOffset.TryParse(record.SourceTimestamp, out var sourceTime)
            ? sourceTime
            : record.ObservedAt;
    }

    private void PostToUi(Action action)
    {
        if (_exiting || _uiInvoker.IsDisposed || !_uiInvoker.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiInvoker.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // UI 正在退出。
        }
    }

    private void OnEventObserved(CodexEventRecord record)
    {
        if (!record.IsCyber)
        {
            RefreshStatusAndTray();
            return;
        }

        if (!record.IsTest && !RegisterRecentCyberEvent(record))
        {
            return;
        }

        RegisterHistoryRecord(record);
        _hasPendingRealAlerts = true;
        _statusForm?.AddCyberEvent(record);
        SetAlertTrayState();
        if (_suspendWarningMaintenance)
        {
            _deferredWarningRecords.Add(record);
        }
        else
        {
            ShowOrUpdateWarning(record);
            SystemSounds.Hand.Play();
        }
        try
        {
            _privacyLog.AppendCyberEvent(record);
        }
        catch (Exception exception)
        {
            _privacyLogError = $"隐私日志写入失败：{exception.GetType().Name}";
            _statusForm?.ReportFault(_privacyLogError);
            RenderTrayState();
        }
    }

    private void ShowOrUpdateWarning(CodexEventRecord record)
    {
        if (_testWarningForm is { IsDisposed: false })
        {
            _testWarningForm.CloseForApplicationExit();
            _testWarningForm = null;
        }

        if (_warningForm is null || _warningForm.IsDisposed)
        {
            _warningForm = new WarningForm(
                record,
                OpenLogsDirectory,
                (Icon)_alertIcon.Clone());
            _warningForm.Acknowledged += OnWarningAcknowledged;
            _warningForm.FormClosed += (_, _) =>
            {
                _warningForm = null;
                RenderTrayState();
            };
        }
        else
        {
            _warningForm.AddEvent(record);
        }

        _warningForm.ShowPersistent();
    }

    private void ShowTestWarning()
    {
        if (_warningForm is { IsDisposed: false })
        {
            _warningForm.ShowPersistent();
            return;
        }

        var testRecord = new CodexEventRecord(
            CodexEventKind.TestWarning,
            DateTimeOffset.Now,
            DateTimeOffset.Now.ToString("o"),
            "TEST-TURN",
            "TEST_CYBER_WARNING",
            "这是界面测试，不代表真实 Cyber 命中",
            "测试事件",
            $"test-event|{Guid.NewGuid():N}",
            -1,
            IsCyber: true,
            IsTest: true);

        if (_testWarningForm is null || _testWarningForm.IsDisposed)
        {
            _testWarningForm = new WarningForm(
                testRecord,
                OpenLogsDirectory,
                (Icon)_alertIcon.Clone());
            _testWarningForm.FormClosed += (_, _) => _testWarningForm = null;
        }
        else
        {
            _testWarningForm.AddEvent(testRecord);
        }

        _testWarningForm.ShowPersistent();
        SystemSounds.Hand.Play();
    }

    private void OnWarningAcknowledged(IReadOnlyList<CodexEventRecord> records)
    {
        try
        {
            _pendingAlerts.Acknowledge(records);
            _pendingStoreError = null;
            _hasPendingRealAlerts = _pendingAlerts.GetPendingRecords().Count > 0;
        }
        catch (Exception exception)
        {
            _pendingStoreError = $"确认状态保存失败：{exception.GetType().Name}";
            _statusForm?.ReportFault(_pendingStoreError);
            _hasPendingRealAlerts = true;
            PostToUi(() =>
            {
                foreach (var record in records.Where(record => !record.IsTest))
                {
                    ShowOrUpdateWarning(record);
                }
                RenderTrayState(forceAlert: true);
            });
            return;
        }

        try
        {
            _privacyLog.AppendAcknowledgement(records);
            _privacyLogError = null;
        }
        catch (Exception exception)
        {
            _privacyLogError = $"确认日志写入失败：{exception.GetType().Name}";
            _statusForm?.ReportFault(_privacyLogError);
        }

        _statusForm?.MarkAllAcknowledged();
        RenderTrayState();
    }

    private bool RegisterRecentCyberEvent(CodexEventRecord record)
    {
        if (!_displayedCyberEventKeys.Add(record.EventKey))
        {
            return false;
        }

        _recentCyberEvents.Add(record);
        _displayedCyberEventOrder.Enqueue(record.EventKey);
        while (_displayedCyberEventOrder.Count > 20_000)
        {
            _displayedCyberEventKeys.Remove(_displayedCyberEventOrder.Dequeue());
        }
        if (_recentCyberEvents.Count > 200)
        {
            _recentCyberEvents.RemoveAt(0);
        }
        return true;
    }

    private void RegisterHistoryRecord(CodexEventRecord record)
    {
        if (record.IsTest)
        {
            return;
        }

        _historyRecords[record.HistoryKey] = record;
        if (_lastHistoryResult is not null)
        {
            _lastHistoryResult = _lastHistoryResult with
            {
                Records = _historyRecords.Values
                    .OrderByDescending(GetHistoryEventTime)
                    .ThenByDescending(item => item.TurnId, StringComparer.Ordinal)
                    .ToArray()
            };
        }
    }

    private string? CurrentHealthError =>
        _unhandledError ?? _pendingStoreError ?? _privacyLogError ?? _monitorFaultError;

    private void RefreshStatusAndTray()
    {
        _statusForm?.UpdateMonitorStatus(_monitor);
        var currentHealthError = CurrentHealthError;
        if (!string.IsNullOrWhiteSpace(currentHealthError))
        {
            _statusForm?.ReportFault(currentHealthError);
        }
        RenderTrayState();
    }

    private void SetAlertTrayState()
    {
        RenderTrayState(forceAlert: true);
    }

    private void SetNormalTrayState()
    {
        RenderTrayState();
    }

    private void RenderTrayState(bool forceAlert = false)
    {
        var hasAlert = forceAlert ||
                       _hasPendingRealAlerts ||
                       _deferredWarningRecords.Count > 0 ||
                       _warningForm is { IsDisposed: false };
        if (hasAlert)
        {
            _notifyIcon.Icon = _alertIcon;
            _notifyIcon.Text = "Codex Cyber 监测器｜⚠ 待确认警告";
            _trayStatusItem.Text = "⚠ 检测到 Cyber 事件，等待确认";
            _trayStatusItem.ForeColor = Color.FromArgb(196, 43, 28);
            _showWarningItem.Enabled = true;
            return;
        }

        _showWarningItem.Enabled = false;
        if (!string.IsNullOrWhiteSpace(CurrentHealthError))
        {
            _notifyIcon.Icon = _errorIcon;
            _notifyIcon.Text = "Codex Cyber 监测器｜监测异常";
            _trayStatusItem.Text = "● 监测出现异常";
            _trayStatusItem.ForeColor = Color.FromArgb(255, 140, 0);
            return;
        }

        _notifyIcon.Icon = _normalIcon;
        _notifyIcon.Text = "Codex Cyber 监测器｜运行中";
        _trayStatusItem.Text = "● 正在实时监测";
        _trayStatusItem.ForeColor = Color.FromArgb(16, 124, 16);
    }

    private void ShowStatusForm()
    {
        if (_statusForm is null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(
                _sessionsRoot,
                ShowTestWarning,
                () => StartHistoryScan(force: true),
                OpenLogsDirectory,
                (Icon)_normalIcon.Clone());
            _statusForm.FormClosed += (_, _) => _statusForm = null;
            if (_lastHistoryResult is null)
            {
                _statusForm.SetHistoryLoading();
                foreach (var record in _recentCyberEvents)
                {
                    _statusForm.AddCyberEvent(record);
                }
            }
            else
            {
                ApplyHistoryToStatusForm();
            }
        }

        RefreshStatusAndTray();
        _statusForm.ShowFromTray();
    }

    private void OpenLogsDirectory()
    {
        Directory.CreateDirectory(_privacyLog.LogsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = _privacyLog.LogsDirectory,
            UseShellExecute = true
        });
    }

    private void UpdateStartupRegistration()
    {
        if (_updatingStartupItem)
        {
            return;
        }

        try
        {
            _startupRegistration.SetEnabled(_startupItem.Checked);
        }
        catch (Exception exception)
        {
            _updatingStartupItem = true;
            try
            {
                _startupItem.Checked = _startupRegistration.IsEnabled;
            }
            finally
            {
                _updatingStartupItem = false;
            }
            ShowOwnedMessageBox(
                $"更新开机启动设置失败：\n{exception.Message}",
                "Codex Cyber 实时监测器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1);
            ResumeWarningPresentation();
        }
    }

    private void OnMonitorFault(string message)
    {
        try
        {
            _privacyLog.AppendMonitorError(message);
        }
        catch (Exception exception)
        {
            // 告警显示不依赖审计日志。
            _privacyLogError = $"监测错误日志写入失败：{exception.GetType().Name}";
        }

        _monitorFaultError = message;
        _statusForm?.ReportFault(message);
        RenderTrayState();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason == SessionSwitchReason.SessionUnlock &&
            _warningForm is { IsDisposed: false })
        {
            _pendingRefocus = true;
        }
    }

    private void ConfirmAndExit()
    {
        var result = ShowOwnedMessageBox(
            "退出后将停止实时监测。确定退出吗？",
            "退出 Codex Cyber 实时监测器",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result == DialogResult.Yes)
        {
            ExitApplication();
            return;
        }

        ResumeWarningPresentation();
    }

    private DialogResult ShowOwnedMessageBox(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
    {
        _suspendWarningMaintenance = true;
        try
        {
            Form? owner = _warningForm is { IsDisposed: false, Visible: true }
                ? _warningForm
                : _testWarningForm is { IsDisposed: false, Visible: true }
                    ? _testWarningForm
                    : _statusForm is { IsDisposed: false, Visible: true }
                        ? _statusForm
                        : null;
            return owner is null
                ? MessageBox.Show(text, caption, buttons, icon, defaultButton)
                : MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
        }
        finally
        {
            _suspendWarningMaintenance = false;
        }
    }

    private void ResumeWarningPresentation()
    {
        _suspendWarningMaintenance = false;
        if (_deferredWarningRecords.Count > 0)
        {
            var deferred = _deferredWarningRecords.ToArray();
            _deferredWarningRecords.Clear();
            foreach (var record in deferred)
            {
                ShowOrUpdateWarning(record);
            }
            SetAlertTrayState();
            SystemSounds.Hand.Play();
        }
        else
        {
            _warningForm?.ShowPersistent();
            _testWarningForm?.ShowPersistent();
        }

        if (_pendingRefocus && _warningForm is { IsDisposed: false })
        {
            _pendingRefocus = false;
            _warningForm.ShowPersistent();
        }
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.AllowClose = true;
            _statusForm.Close();
        }
        if (_warningForm is { IsDisposed: false })
        {
            _warningForm.CloseForApplicationExit();
        }
        if (_testWarningForm is { IsDisposed: false })
        {
            _testWarningForm.CloseForApplicationExit();
        }

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _startupTimer?.Stop();
            _startupTimer?.Dispose();
            _historyScanCancellation?.Cancel();
            try
            {
                _pollTask?.Wait();
                _historyScanTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 退出阶段继续释放资源。
            }
            _historyScanCancellation?.Dispose();
            _monitor.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _trayMenu.Dispose();
            _uiInvoker.Dispose();
            _normalIcon.Dispose();
            _alertIcon.Dispose();
            _errorIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
