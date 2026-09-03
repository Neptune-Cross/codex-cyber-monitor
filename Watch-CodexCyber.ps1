[CmdletBinding()]
param(
    [string]$SessionsRoot = (Join-Path $HOME '.codex\sessions'),

    [ValidateRange(200, 10000)]
    [int]$PollMilliseconds = 750,

    [switch]$OnlyCyber,

    [switch]$ReplayExisting,

    [string]$LogPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'CodexCyber.Common.ps1')

if (-not (Test-Path -LiteralPath $SessionsRoot -PathType Container)) {
    throw "未找到 Codex sessions 目录：$SessionsRoot"
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Join-Path $PSScriptRoot 'logs'
    if (-not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory | Out-Null
    }
    $LogPath = Join-Path $logDirectory ("codex-cyber-monitor-{0}.jsonl" -f (Get-Date -Format 'yyyyMMdd'))
}
else {
    $resolvedLogParent = Split-Path -Parent $LogPath
    if (-not [string]::IsNullOrWhiteSpace($resolvedLogParent) -and
        -not (Test-Path -LiteralPath $resolvedLogParent)) {
        New-Item -ItemType Directory -Path $resolvedLogParent | Out-Null
    }
}

function New-TailState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [bool]$StartAtBeginning
    )

    $item = Get-Item -LiteralPath $Path
    [pscustomobject]@{
        Offset  = if ($StartAtBeginning) { [long]0 } else { [long]$item.Length }
        Pending = [byte[]]::new(0)
    }
}

function Read-AppendedUtf8Lines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$State
    )

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite
    )

    try {
        if ($stream.Length -lt $State.Offset) {
            $State.Offset = [long]0
            $State.Pending = [byte[]]::new(0)
        }

        $available = [long]($stream.Length - $State.Offset)
        if ($available -le 0) {
            return
        }
        if ($available -gt [int]::MaxValue) {
            throw "单次新增日志超过 2 GB，停止读取：$Path"
        }

        [void]$stream.Seek($State.Offset, [IO.SeekOrigin]::Begin)
        $newBytes = [byte[]]::new([int]$available)
        $totalRead = 0
        while ($totalRead -lt $newBytes.Length) {
            $read = $stream.Read($newBytes, $totalRead, $newBytes.Length - $totalRead)
            if ($read -eq 0) {
                break
            }
            $totalRead += $read
        }

        if ($totalRead -lt $newBytes.Length) {
            $actualBytes = [byte[]]::new($totalRead)
            [Array]::Copy($newBytes, 0, $actualBytes, 0, $totalRead)
            $newBytes = $actualBytes
        }

        $State.Offset += [long]$totalRead

        $combined = [byte[]]::new($State.Pending.Length + $newBytes.Length)
        if ($State.Pending.Length -gt 0) {
            [Array]::Copy($State.Pending, 0, $combined, 0, $State.Pending.Length)
        }
        if ($newBytes.Length -gt 0) {
            [Array]::Copy($newBytes, 0, $combined, $State.Pending.Length, $newBytes.Length)
        }

        $lineStart = 0
        for ($index = 0; $index -lt $combined.Length; $index++) {
            if ($combined[$index] -ne 10) {
                continue
            }

            $lineLength = $index - $lineStart
            if ($lineLength -gt 0 -and $combined[$index - 1] -eq 13) {
                $lineLength--
            }

            [Text.Encoding]::UTF8.GetString($combined, $lineStart, $lineLength)
            $lineStart = $index + 1
        }

        $remainingLength = $combined.Length - $lineStart
        $State.Pending = [byte[]]::new($remainingLength)
        if ($remainingLength -gt 0) {
            [Array]::Copy($combined, $lineStart, $State.Pending, 0, $remainingLength)
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Write-MonitorRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Record
    )

    $displayTime = if ([string]::IsNullOrWhiteSpace($Record.Timestamp)) {
        Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    }
    else {
        $Record.Timestamp
    }

    $message = "[$displayTime] [$($Record.Result)] turn=$($Record.TurnId) $($Record.Detail)"
    $color = switch ($Record.Result) {
        'CYBER_BLOCK' { 'Red' }
        'CYBER_REROUTE' { 'Red' }
        'CYBER_VERIFICATION' { 'Yellow' }
        'CYBER_BUFFERING' { 'Yellow' }
        'OTHER_ERROR' { 'DarkYellow' }
        default { 'Green' }
    }

    Write-Host $message -ForegroundColor $color

    $logRecord = [ordered]@{
        observed_at = (Get-Date).ToString('o')
        timestamp   = $Record.Timestamp
        turn_id     = $Record.TurnId
        result      = $Record.Result
        detail      = $Record.Detail
        is_cyber    = $Record.IsCyber
        source_path = $Record.SourcePath
    }
    ($logRecord | ConvertTo-Json -Compress) |
        Add-Content -LiteralPath $LogPath -Encoding UTF8
}

$states = @{}
$seen = @{}
$initialFiles = @(Get-ChildItem -LiteralPath $SessionsRoot -Recurse -File -Filter 'rollout-*.jsonl')
foreach ($file in $initialFiles) {
    $states[$file.FullName] = New-TailState -Path $file.FullName -StartAtBeginning:$ReplayExisting
}

Write-Host 'Codex Cyber 实时监测已启动。按 Ctrl+C 停止。' -ForegroundColor Cyan
Write-Host "监测目录：$SessionsRoot" -ForegroundColor Cyan
Write-Host "记录文件：$LogPath" -ForegroundColor Cyan
Write-Host "现有 rollout 文件：$($initialFiles.Count)；回放现有内容：$([bool]$ReplayExisting)" -ForegroundColor Cyan
Write-Host '判定基于结构化事件，不读取或保存 prompt/error.message。' -ForegroundColor Cyan

while ($true) {
    $files = @(Get-ChildItem -LiteralPath $SessionsRoot -Recurse -File -Filter 'rollout-*.jsonl')

    foreach ($file in $files) {
        if (-not $states.ContainsKey($file.FullName)) {
            $states[$file.FullName] = New-TailState -Path $file.FullName -StartAtBeginning:$true
        }

        $state = $states[$file.FullName]
        foreach ($line in (Read-AppendedUtf8Lines -Path $file.FullName -State $state)) {
            $isCandidate =
                $line.Contains('task_complete', [StringComparison]::Ordinal) -or
                $line.Contains('model_reroute', [StringComparison]::Ordinal) -or
                $line.Contains('model_verification', [StringComparison]::Ordinal) -or
                $line.Contains('safety_buffering', [StringComparison]::Ordinal)

            if (-not $isCandidate) {
                continue
            }

            try {
                $event = $line | ConvertFrom-Json -Depth 100 -ErrorAction Stop
            }
            catch {
                Write-Warning "跳过一条不可解析的完整 JSONL 记录：$($file.FullName)"
                continue
            }

            $record = ConvertTo-CodexCyberRecord `
                -Event $event `
                -SourcePath $file.FullName `
                -IncludeNormalCompletion:(-not $OnlyCyber)

            if ($null -eq $record) {
                continue
            }

            $key = '{0}|{1}|{2}|{3}' -f $record.Timestamp, $record.TurnId, $record.Result, $file.FullName
            if ($seen.ContainsKey($key)) {
                continue
            }
            $seen[$key] = $true

            Write-MonitorRecord -Record $record
        }
    }

    Start-Sleep -Milliseconds $PollMilliseconds
}

