[CmdletBinding()]
param(
    [string[]]$Roots = @(
        (Join-Path $HOME '.codex\sessions'),
        (Join-Path $HOME '.codex\archived_sessions')
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'CodexCyber.Common.ps1')

$existingRoots = @($Roots | Where-Object { Test-Path -LiteralPath $_ })
if ($existingRoots.Count -eq 0) {
    throw '未找到 Codex sessions 或 archived_sessions 目录。'
}

$files = @(Get-ChildItem -LiteralPath $existingRoots -Recurse -File -Filter 'rollout-*.jsonl')
$records = [System.Collections.Generic.List[object]]::new()
$parsedCandidates = 0

foreach ($file in $files) {
    $stream = [IO.FileStream]::new(
        $file.FullName,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite
    )
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)

    try {
        while (($line = $reader.ReadLine()) -ne $null) {
            $isCandidate =
                $line.Contains('task_complete', [StringComparison]::Ordinal) -or
                $line.Contains('model_reroute', [StringComparison]::Ordinal) -or
                $line.Contains('model_verification', [StringComparison]::Ordinal) -or
                $line.Contains('safety_buffering', [StringComparison]::Ordinal)

            if (-not $isCandidate) {
                continue
            }

            $parsedCandidates++
            try {
                $event = $line | ConvertFrom-Json -Depth 100 -ErrorAction Stop
            }
            catch {
                Write-Warning "跳过一条不可解析的 JSONL 记录：$($file.FullName)"
                continue
            }

            $record = ConvertTo-CodexCyberRecord -Event $event -SourcePath $file.FullName
            if ($null -ne $record) {
                $records.Add($record)
            }
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

Write-Host "已扫描文件：$($files.Count)" -ForegroundColor Cyan
Write-Host "已解析候选记录：$parsedCandidates" -ForegroundColor Cyan
Write-Host "已记录 Cyber 事件：$($records.Count)" -ForegroundColor Cyan

if ($records.Count -eq 0) {
    Write-Host '结论：本地 rollout 历史中未发现结构化 Cyber 事件。' -ForegroundColor Green
}
else {
    Write-Host '发现以下结构化 Cyber 事件：' -ForegroundColor Red
    $records |
        Sort-Object Timestamp |
        Format-Table Timestamp, TurnId, Result, Detail, SourcePath -AutoSize
}

