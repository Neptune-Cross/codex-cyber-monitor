using System.Diagnostics;
using CodexCyberMonitor.Domain;
using CodexCyberMonitor.Infrastructure;
using Microsoft.Win32;

namespace CodexCyberMonitor.UI;

internal sealed class WarningForm : Form
{
    private static readonly Color DangerRed = Color.FromArgb(196, 43, 28);
    private static readonly Color DeepRed = Color.FromArgb(164, 38, 44);
    private static readonly Color SoftRed = Color.FromArgb(255, 244, 244);
    private readonly List<CodexEventRecord> _events = [];
    private readonly Action _openLogs;
    private readonly Icon _ownedIcon;
    private readonly System.Windows.Forms.Timer _unlockTimer;
    private readonly Label _titleLabel;
    private readonly Label _countLabel;
    private readonly Label _resultValue;
    private readonly Label _detailValue;
    private readonly Label _timeValue;
    private readonly Label _turnValue;
    private readonly Label _fileValue;
    private readonly Button _acknowledgeButton;
    private bool _acknowledgementRaised;
    private bool _canUserClose;
    private bool _acknowledgedByUser;
    private bool _closingForApplicationExit;

    public WarningForm(CodexEventRecord initialEvent, Action openLogs, Icon icon)
    {
        _openLogs = openLogs;
        _ownedIcon = icon;
        Icon = _ownedIcon;
        Text = "⚠ Codex Cyber 警告";
        Width = 760;
        Height = 520;
        MinimumSize = new Size(700, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = DeepRed;
        Padding = new Padding(4);
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SoftRed,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = DangerRed,
            Padding = new Padding(24, 12, 24, 8),
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 2
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);

        _titleLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "⚠  检测到 Codex Cyber 事件",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(_titleLabel, 0, 0);

        var subtitle = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "此红色警告会持续置顶显示，直到你手动关闭。",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(subtitle, 0, 1);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = SoftRed,
            Padding = new Padding(28, 14, 28, 8),
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 3
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 1);

        _countLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = DeepRed,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point),
            Text = "已捕获 1 个结构化 Cyber 事件",
            TextAlign = ContentAlignment.MiddleLeft
        };
        body.Controls.Add(_countLabel, 0, 0);

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            BackColor = Color.White,
            Padding = new Padding(14, 8, 14, 8),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 5; row++)
        {
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        }
        body.Controls.Add(details, 0, 1);

        _resultValue = AddDetailRow(details, 0, "事件类型");
        _detailValue = AddDetailRow(details, 1, "结构化标识");
        _timeValue = AddDetailRow(details, 2, "检测时间");
        _turnValue = AddDetailRow(details, 3, "Turn ID");
        _fileValue = AddDetailRow(details, 4, "来源文件");

        var note = new Label
        {
            Dock = DockStyle.Fill,
            Text = "本窗口仅展示本地结构化事件元数据，不读取或显示请求正文。",
            ForeColor = Color.FromArgb(96, 94, 92),
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 12, 0, 0)
        };
        body.Controls.Add(note, 0, 2);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(18, 15, 18, 10),
            Margin = Padding.Empty
        };
        root.Controls.Add(footer, 0, 2);

        _acknowledgeButton = CreateButton("我已知晓并关闭警告", DangerRed, Color.White, 190);
        _acknowledgeButton.Enabled = false;
        _acknowledgeButton.Click += (_, _) =>
        {
            if (_canUserClose)
            {
                _acknowledgedByUser = true;
                Close();
            }
        };
        footer.Controls.Add(_acknowledgeButton);

        var openLogsButton = CreateButton("打开日志目录", Color.White, Color.FromArgb(32, 31, 30), 130);
        openLogsButton.Click += (_, _) => _openLogs();
        footer.Controls.Add(openLogsButton);

        var copyButton = CreateButton("复制诊断信息", Color.White, Color.FromArgb(32, 31, 30), 130);
        copyButton.Click += (_, _) => CopyDiagnosticInformation();
        footer.Controls.Add(copyButton);

        AddEvent(initialEvent);

        _unlockTimer = new System.Windows.Forms.Timer { Interval = 750 };
        _unlockTimer.Tick += (_, _) =>
        {
            _unlockTimer.Stop();
            _canUserClose = true;
            _acknowledgeButton.Enabled = true;
        };
        Shown += (_, _) =>
        {
            CenterWithin(Screen.FromControl(this));
            _unlockTimer.Start();
        };
        DpiChanged += (_, _) => ScheduleReposition();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public event Action<IReadOnlyList<CodexEventRecord>>? Acknowledged;

    public void AddEvent(CodexEventRecord record)
    {
        if (_events.Any(item => item.EventKey == record.EventKey))
        {
            return;
        }

        _events.Add(record);
        var latest = _events[^1];
        _titleLabel.Text = _events.Count == 1
            ? "⚠  检测到 Codex Cyber 事件"
            : $"⚠  检测到 {_events.Count} 个 Codex Cyber 事件";
        _countLabel.Text = $"已捕获 {_events.Count} 个结构化 Cyber 事件；当前显示最新一条";
        _resultValue.Text = latest.Result;
        _detailValue.Text = latest.Detail;
        _timeValue.Text = FormatTimestamp(latest);
        _turnValue.Text = latest.ShortTurnId;
        _fileValue.Text = latest.SourceFileName;
    }

    public void ShowPersistent()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var screen = foreground != IntPtr.Zero
            ? Screen.FromHandle(foreground)
            : Screen.PrimaryScreen ?? Screen.AllScreens[0];
        CenterWithin(screen);

        if (!Visible)
        {
            Show();
        }

        CenterWithin(screen);

        WindowState = FormWindowState.Normal;
        TopMost = true;
        BringToFront();
        Activate();
        NativeMethods.BringAlertToFront(this);
    }

    public void MaintainPersistentVisibility()
    {
        if (IsDisposed)
        {
            return;
        }

        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            ShowPersistent();
            return;
        }

        TopMost = true;
        NativeMethods.EnsureTopmost(this);
    }

    public void CloseForApplicationExit()
    {
        _closingForApplicationExit = true;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        if (_acknowledgedByUser && !_acknowledgementRaised)
        {
            _acknowledgementRaised = true;
            Acknowledged?.Invoke(_events.ToArray());
        }

        base.OnFormClosed(eventArgs);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!_closingForApplicationExit && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            if (!_canUserClose)
            {
                eventArgs.Cancel = true;
                return;
            }

            _acknowledgedByUser = true;
        }

        base.OnFormClosing(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _unlockTimer.Stop();
            _unlockTimer.Dispose();
            _ownedIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CenterWithin(Screen screen)
    {
        var area = screen.WorkingArea;
        const int margin = 12;
        var scale = Math.Max(1F, DeviceDpi / 96F);
        var maximumWidth = Math.Max(320, area.Width - (margin * 2));
        var maximumHeight = Math.Max(320, area.Height - (margin * 2));
        var targetWidth = Math.Min((int)Math.Round(760 * scale), maximumWidth);
        var targetHeight = Math.Min((int)Math.Round(520 * scale), maximumHeight);

        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        Size = new Size(targetWidth, targetHeight);
        MinimumSize = new Size(
            Math.Min((int)Math.Round(560 * scale), targetWidth),
            Math.Min((int)Math.Round(400 * scale), targetHeight));
        MaximumSize = new Size(
            Math.Min((int)Math.Round(960 * scale), maximumWidth),
            Math.Min((int)Math.Round(640 * scale), maximumHeight));

        var centeredX = area.Left + Math.Max(0, (area.Width - Width) / 2);
        var centeredY = area.Top + Math.Max(0, (area.Height - Height) / 2);
        var maximumX = Math.Max(area.Left, area.Right - Width);
        var maximumY = Math.Max(area.Top, area.Bottom - Height);
        Location = new Point(
            Math.Clamp(centeredX, area.Left, maximumX),
            Math.Clamp(centeredY, area.Top, maximumY));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs eventArgs)
    {
        ScheduleReposition();
    }

    private void ScheduleReposition()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(() => CenterWithin(Screen.FromControl(this)));
        }
        catch (InvalidOperationException)
        {
            // 窗口正在退出。
        }
    }

    private static Label AddDetailRow(TableLayoutPanel table, int row, string name)
    {
        var nameLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = name,
            ForeColor = Color.FromArgb(96, 94, 92),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(5, 0, 0, 0)
        };
        var valueLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "—",
            ForeColor = Color.FromArgb(32, 31, 30),
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(6, 0, 0, 0)
        };

        table.Controls.Add(nameLabel, 0, row);
        table.Controls.Add(valueLabel, 1, row);
        return valueLabel;
    }

    private static Button CreateButton(string text, Color backColor, Color foreColor, int width)
    {
        return new Button
        {
            Text = text,
            Width = width,
            Height = 38,
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0),
            UseVisualStyleBackColor = false
        };
    }

    private static string FormatTimestamp(CodexEventRecord record)
    {
        return DateTimeOffset.TryParse(record.SourceTimestamp, out var sourceTime)
            ? sourceTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : record.ObservedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void CopyDiagnosticInformation()
    {
        var latest = _events[^1];
        var text = string.Join(
            Environment.NewLine,
            $"事件数量: {_events.Count}",
            $"事件类型: {latest.Result}",
            $"结构化标识: {latest.Detail}",
            $"检测时间: {FormatTimestamp(latest)}",
            $"Turn ID: {latest.ShortTurnId}",
            $"来源文件: {latest.SourceFileName}");
        Clipboard.SetText(text);
    }
}
