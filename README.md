# Codex Cyber 实时监测器

Windows 托盘常驻程序，实时增量监测本机 Codex 会话日志中的结构化 Cyber 事件。捕获后会立即弹出红色置顶警告窗，并持续保持可见，直到用户手动确认关闭。

## 成品入口

- 单文件程序：`dist\win-x64\CodexCyberMonitor.exe`
- 便携发布包：`release\CodexCyberMonitor-win-x64-1.1.0.zip`
- 一键安装：解压发布包后运行 `安装并启动.ps1`
- 默认安装目录：`%LOCALAPPDATA%\Programs\CodexCyberMonitor`

程序为 Windows x64 自包含单文件，不要求目标机器另装 .NET Runtime。

## 主要功能

- 常驻 Windows 当前用户托盘，默认随登录自动启动。
- 同时监测：
  - `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`
  - `%USERPROFILE%\.codex\archived_sessions\**\rollout-*.jsonl`
- 只解析结构化 JSON 字段，不因请求正文出现 `cyber_policy` 等文本而误报。
- 红色告警窗持续置顶；Win+D、解锁或最小化后会自动恢复可见。
- 多个事件合并在同一个警告窗内，用户确认前不会自动消失。
- 未确认告警写入本地状态；程序重启后继续显示。
- 正常、待确认和监测异常分别使用绿色、红色和橙色托盘状态。
- 监测面板自动扫描并显示 `sessions` 与 `archived_sessions` 中的全部历史 Cyber 记录。
- 历史列表支持手动刷新、双击查看完整详情、右键复制完整记录，并按 Turn ID 与事件类型去重。
- 内置状态面板、测试警告、隐私日志、开机启动开关及卸载脚本。

首次运行会把已经完整写入的旧日志建立为实时告警基线；旧事件不会突然弹窗，但会完整显示在监测面板的历史列表中。从启动时正在写入的请求和之后的新请求开始实时告警。

## 捕获条件

| 结果 | 结构化条件 |
|---|---|
| `CYBER_BLOCK` | `task_complete` 且 `error.codex_error_info == cyber_policy` |
| `CYBER_REROUTE` | `model_reroute` 且 `reason == high_risk_cyber_activity` |
| `CYBER_VERIFICATION` | `model_verification.verifications[]` 包含 `trusted_access_for_cyber` |
| `CYBER_BUFFERING` | `safety_buffering.use_cases[]` 包含 `cyber` |

`NO_RECORDED_CYBER_POLICY` 只用于状态计数，不弹红窗。

## 安装与运行

在发布目录执行：

```powershell
$ErrorActionPreference = 'Stop'
.\安装并启动.ps1
```

安装后可从开始菜单打开“Codex Cyber 实时监测器”，或在任务栏右侧 `^` 的隐藏图标区找到盾牌图标。

直接运行单文件程序也可以：

```powershell
$ErrorActionPreference = 'Stop'
& '.\CodexCyberMonitor.exe' --show
```

界面测试：

```powershell
$ErrorActionPreference = 'Stop'
& '.\CodexCyberMonitor.exe' --test-alert
```

## 托盘操作

- 左键：优先显示当前红色警告，否则打开监测面板。
- 双击：打开监测面板。
- 右键：查看全部历史记录、显示警告、测试警告、打开日志、切换开机启动或退出。
- 关闭监测面板只会隐藏到托盘；退出需从托盘菜单明确确认。

## 历史记录

打开监测面板后，程序会在后台结构化扫描两个会话目录，并显示全部 `CYBER_BLOCK`、`CYBER_REROUTE`、`CYBER_VERIFICATION` 与 `CYBER_BUFFERING`。扫描不会读取或显示请求正文，也不会触发红色警告。

- 点击“刷新历史记录”重新扫描。
- 双击任意记录查看完整时间、Turn ID、结构化标识、来源文件和字节偏移。
- 右键记录可复制完整信息。

## 本地数据与隐私

运行数据位于：

```text
%LOCALAPPDATA%\CodexCyberMonitor
```

日志不保存 prompt、回复正文、原始 JSONL 或 `error.message`。Turn ID 与源标识使用安装级随机盐生成短摘要；日志默认保留 30 天。

红窗表示本机日志出现了上述结构化事件，不表示账号 strike 数量，也不等同于封禁通知。

## 卸载

```powershell
$ErrorActionPreference = 'Stop'
& "$env:LOCALAPPDATA\Programs\CodexCyberMonitor\卸载.ps1"
```

同时删除游标、待确认状态和隐私日志：

```powershell
$ErrorActionPreference = 'Stop'
& "$env:LOCALAPPDATA\Programs\CodexCyberMonitor\卸载.ps1" -RemoveData
```

## 构建与自检

```powershell
$ErrorActionPreference = 'Stop'
.\build.ps1
```

构建脚本会执行解析器、增量读取、失败重试和持久化自检，再生成单文件 EXE、SHA-256 与 ZIP 包。

保留的 PowerShell 命令行工具：

- `Audit-CodexCyberHistory.ps1`：一次性历史审计。
- `Watch-CodexCyber.ps1`：控制台实时监测。
- `tests\Test-CodexCyberParser.ps1`：PowerShell 解析器测试。

## https://linux.do
