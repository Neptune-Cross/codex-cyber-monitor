using System.Diagnostics;
using CodexCyberMonitor.Domain;
using CodexCyberMonitor.Monitoring;

namespace CodexCyberMonitor.UI;

internal sealed class StatusForm : Form
{
    private static readonly Color Navy = Color.FromArgb(15, 23, 42);
    private static readonly Color SuccessGreen = Color.FromArgb(16, 124, 16);
    private static readonly Color DangerRed = Color.FromArgb(196, 43, 28);
    private readonly Label _statusLabel;
    private readonly Label _turnsValue;
    private readonly Label _cyberValue;
    private readonly Label _scanValue;
    private readonly Label _historyStatusLabel;
    private readonly Label _pathLabel;
    private readonly DataGridView _eventGrid;
    private readonly Action _testWarning;
    private readonly Action _refreshHistory;
    private readonly Action _openLogs;
    private readonly string _sessionsPathText;
    private readonly Icon _ownedIcon;
    private readonly ContextMenuStrip _historyMenu;
    private readonly Dictionary<string, DataGridViewRow> _historyRows = new(StringComparer.Ordinal);

    public StatusForm(
        string sessionsRoot,
        Action testWarning,
        Action refreshHistory,
        Action openLogs,
        Icon icon)
    {
        _testWarning = testWarning;
        _refreshHistory = refreshHistory;
        _openLogs = openLogs;
        var codexRoot = Directory.GetParent(sessionsRoot)?.FullName ?? sessionsRoot;
        _sessionsPathText = $"监测目录：{codexRoot}\\sessions + archived_sessions";
        _ownedIcon = icon;
        Icon = _ownedIcon;
        Text = "Codex Cyber 实时监测器";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 920;
        Height = 600;
        MinimumSize = new Size(760, 520);
        BackColor = Color.FromArgb(247, 247, 248);
        Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Navy,
            Padding = new Padding(24, 10, 24, 8),
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = 2
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "🛡  Codex Cyber 实时监测",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(title, 0, 0);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "● 正在实时监测",
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleRight
        };
        header.Controls.Add(_statusLabel, 1, 0);
        header.SetRowSpan(_statusLabel, 2);

        var subtitle = new Label
        {
            Dock = DockStyle.Fill,
            Text = "监测本机 Codex 会话中的结构化策略事件",
            ForeColor = Color.FromArgb(203, 213, 225),
            TextAlign = ContentAlignment.MiddleLeft
        };
        header.Controls.Add(subtitle, 0, 1);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 14, 18, 8),
            Margin = Padding.Empty
        };
        for (var column = 0; column < 3; column++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        }
        root.Controls.Add(cards, 0, 1);

        _turnsValue = AddCard(cards, 0, "本次运行请求", "0", SuccessGreen);
        _cyberValue = AddCard(cards, 1, "历史 Cyber 记录", "…", DangerRed);
        _scanValue = AddCard(cards, 2, "最近扫描", "—", Color.FromArgb(0, 103, 192));

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 8, 18, 8),
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 2
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(content, 0, 2);

        var sectionHeader = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        content.Controls.Add(sectionHeader, 0, 0);

        var sectionTitle = new Label
        {
            Dock = DockStyle.Left,
            Width = 240,
            Text = "全部历史 Cyber 记录",
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(32, 31, 30),
            TextAlign = ContentAlignment.MiddleLeft
        };
        sectionHeader.Controls.Add(sectionTitle);

        _historyStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在扫描 sessions 与 archived_sessions…",
            ForeColor = Color.FromArgb(96, 94, 92),
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        };
        sectionHeader.Controls.Add(_historyStatusLabel);
        sectionTitle.BringToFront();

        _eventGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
        };
        _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", FillWeight = 28 });
        _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "事件", FillWeight = 23 });
        _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Turn ID", FillWeight = 24 });
        _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "来源文件", FillWeight = 17 });
        _eventGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 12 });
        _eventGrid.CellDoubleClick += (_, _) => ShowSelectedHistoryDetails();
        _historyMenu = new ContextMenuStrip();
        var copyHistoryItem = new ToolStripMenuItem("复制选中记录的完整信息");
        copyHistoryItem.Click += (_, _) => CopySelectedHistoryRecord();
        _historyMenu.Items.Add(copyHistoryItem);
        _eventGrid.ContextMenuStrip = _historyMenu;
        content.Controls.Add(_eventGrid, 0, 1);

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(18, 10, 18, 8),
            Margin = Padding.Empty
        };
        root.Controls.Add(footer, 0, 3);

        _pathLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = _sessionsPathText,
            ForeColor = Color.FromArgb(96, 94, 92),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        footer.Controls.Add(_pathLabel);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 430,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        footer.Controls.Add(actions);

        var logsButton = CreateButton("打开日志目录", 128);
        logsButton.Click += (_, _) => _openLogs();
        actions.Controls.Add(logsButton);

        var testButton = CreateButton("测试红色警告", 140);
        testButton.Click += (_, _) => _testWarning();
        actions.Controls.Add(testButton);

        var refreshButton = CreateButton("刷新历史记录", 128);
        refreshButton.Click += (_, _) => _refreshHistory();
        actions.Controls.Add(refreshButton);
    }

    public bool AllowClose { get; set; }

    public void UpdateMonitorStatus(RolloutMonitor monitor)
    {
        _turnsValue.Text = monitor.TotalTurns.ToString();
        _scanValue.Text = monitor.LastScanAt.LocalDateTime.ToString("HH:mm:ss");
        _statusLabel.Text = "● 正在实时监测";
        _statusLabel.ForeColor = Color.FromArgb(126, 231, 135);
        _pathLabel.Text = _sessionsPathText;
    }

    public void SetHistoryLoading()
    {
        _historyStatusLabel.Text = "正在扫描 sessions 与 archived_sessions…";
        _historyStatusLabel.ForeColor = Color.FromArgb(0, 103, 192);
        if (_historyRows.Count == 0)
        {
            _cyberValue.Text = "…";
        }
    }

    public void SetHistoryRecords(
        IReadOnlyList<CodexEventRecord> records,
        IReadOnlySet<string> pendingHistoryKeys,
        HistoryAuditResult result)
    {
        _eventGrid.SuspendLayout();
        try
        {
            _eventGrid.Rows.Clear();
            _historyRows.Clear();
            foreach (var record in records)
            {
                AddHistoryRow(
                    record,
                    pendingHistoryKeys.Contains(record.HistoryKey) ? "待确认" : "历史");
            }
        }
        finally
        {
            _eventGrid.ResumeLayout();
        }

        _cyberValue.Text = records.Count.ToString();
        _historyStatusLabel.Text = result.FilesFailed == 0
            ? $"已扫描 {result.FilesScanned} 个文件｜{result.CompletedAt:HH:mm:ss}"
            : $"已扫描 {result.FilesScanned} 个文件｜{result.FilesFailed} 个读取失败";
        _historyStatusLabel.ForeColor = result.FilesFailed == 0
            ? Color.FromArgb(16, 124, 16)
            : Color.FromArgb(184, 115, 0);
    }

    public void ReportHistoryFault(string message)
    {
        _historyStatusLabel.Text = message;
        _historyStatusLabel.ForeColor = DangerRed;
    }

    public void ReportFault(string message)
    {
        _statusLabel.Text = "● 监测出现异常";
        _statusLabel.ForeColor = Color.FromArgb(255, 185, 0);
        _pathLabel.Text = message;
    }

    public void AddCyberEvent(CodexEventRecord record)
    {
        if (record.IsTest)
        {
            return;
        }

        if (_historyRows.TryGetValue(record.HistoryKey, out var existingRow))
        {
            existingRow.Cells[4].Value = "待确认";
            return;
        }

        AddHistoryRow(record, "待确认", insertAtTop: true);
        _cyberValue.Text = _historyRows.Count.ToString();
    }

    public void MarkAllAcknowledged()
    {
        foreach (DataGridViewRow row in _eventGrid.Rows)
        {
            if (string.Equals(row.Cells[4].Value?.ToString(), "待确认", StringComparison.Ordinal))
            {
                row.Cells[4].Value = "已确认";
            }
        }
    }

    private void AddHistoryRow(
        CodexEventRecord record,
        string status,
        bool insertAtTop = false)
    {
        var time = DateTimeOffset.TryParse(record.SourceTimestamp, out var sourceTime)
            ? sourceTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : record.ObservedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        var row = new DataGridViewRow();
        row.CreateCells(
            _eventGrid,
            time,
            record.Result,
            record.ShortTurnId,
            record.SourceFileName,
            status);
        row.Tag = record;
        if (insertAtTop)
        {
            _eventGrid.Rows.Insert(0, row);
        }
        else
        {
            _eventGrid.Rows.Add(row);
        }
        _historyRows[record.HistoryKey] = row;
    }

    private CodexEventRecord? GetSelectedHistoryRecord()
    {
        return _eventGrid.SelectedRows.Count > 0
            ? _eventGrid.SelectedRows[0].Tag as CodexEventRecord
            : null;
    }

    private void ShowSelectedHistoryDetails()
    {
        var record = GetSelectedHistoryRecord();
        if (record is null)
        {
            return;
        }

        MessageBox.Show(
            this,
            BuildHistoryDetails(record),
            "历史 Cyber 记录详情",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void CopySelectedHistoryRecord()
    {
        var record = GetSelectedHistoryRecord();
        if (record is not null)
        {
            Clipboard.SetText(BuildHistoryDetails(record));
        }
    }

    private static string BuildHistoryDetails(CodexEventRecord record)
    {
        var time = DateTimeOffset.TryParse(record.SourceTimestamp, out var sourceTime)
            ? sourceTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
            : record.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        return string.Join(
            Environment.NewLine,
            $"时间：{time}",
            $"事件：{record.Result}",
            $"结构化标识：{record.Detail}",
            $"Turn ID：{record.TurnId}",
            $"来源文件：{record.SourceFileName}",
            $"完整路径：{record.SourcePath}",
            $"字节偏移：{record.LineOffset}");
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        if (!Visible)
        {
            Show();
        }
        BringToFront();
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!AllowClose && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }

        base.OnFormClosing(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _historyMenu.Dispose();
            _ownedIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Label AddCard(
        TableLayoutPanel parent,
        int column,
        string title,
        string initialValue,
        Color accent)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = new Padding(6),
            Padding = new Padding(16, 10, 16, 8)
        };
        parent.Controls.Add(card, column, 0);

        var titleLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = title,
            ForeColor = Color.FromArgb(96, 94, 92),
            TextAlign = ContentAlignment.MiddleLeft
        };
        card.Controls.Add(titleLabel);

        var valueLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = initialValue,
            ForeColor = accent,
            Font = new Font("Segoe UI", 22F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft
        };
        card.Controls.Add(valueLabel);
        valueLabel.BringToFront();
        return valueLabel;
    }

    private static Button CreateButton(string text, int width)
    {
        return new Button
        {
            Text = text,
            Width = width,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(32, 31, 30),
            Cursor = Cursors.Hand,
            Margin = new Padding(8, 0, 0, 0)
        };
    }
}
