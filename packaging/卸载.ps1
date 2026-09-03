[CmdletBinding()]
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SafeRecursiveRemovalTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TargetPath,

        [Parameter(Mandatory)]
        [string]$ExpectedParentPath
    )

    $fullTarget = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    $fullExpectedParent = [IO.Path]::GetFullPath($ExpectedParentPath).TrimEnd('\')
    $actualParent = [IO.Path]::GetDirectoryName($fullTarget)
    if (-not [string]::Equals(
        $actualParent,
        $fullExpectedParent,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝递归清理非预期目录：$fullTarget"
    }

    if (Test-Path -LiteralPath $fullExpectedParent) {
        $expectedParentItem = Get-Item -LiteralPath $fullExpectedParent -Force
        if (-not $expectedParentItem.PSIsContainer -or
            ($expectedParentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "递归清理的预期父目录不是普通本地目录：$fullExpectedParent"
        }
    }

    if (-not (Test-Path -LiteralPath $fullTarget)) {
        return
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($fullTarget)
    while ($pendingDirectories.Count -gt 0) {
        $currentPath = $pendingDirectories.Pop()
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        if (-not $currentItem.PSIsContainer) {
            throw "拒绝将非目录目标作为递归清理目录：$currentPath"
        }
        if (($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝递归清理 reparse point/junction：$currentPath"
        }

        foreach ($child in Get-ChildItem -LiteralPath $currentPath -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "目录树中存在 reparse point/junction，拒绝递归清理：$($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pendingDirectories.Push($child.FullName)
            }
        }
    }
}

$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsRoot 'CodexCyberMonitor'))
if (-not [string]::Equals(
    [IO.Path]::GetDirectoryName($installDirectory),
    $programsRoot,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝清理非预期目录：$installDirectory"
}

$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexCyberMonitor'))
$expectedDataParent = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')

# 在停止进程或删除启动项前完成所有递归删除目标审核，避免失败时留下半卸载状态。
Assert-SafeRecursiveRemovalTarget `
    -TargetPath $installDirectory `
    -ExpectedParentPath $programsRoot
if ($RemoveData) {
    Assert-SafeRecursiveRemovalTarget `
        -TargetPath $dataRoot `
        -ExpectedParentPath $expectedDataParent
}

$targetExe = Join-Path $installDirectory 'CodexCyberMonitor.exe'
$runningInstalledProcesses = @(Get-Process -Name 'CodexCyberMonitor' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            [string]::Equals($_.Path, $targetExe, [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    })
foreach ($runningProcess in $runningInstalledProcesses) {
    Stop-Process -Id $runningProcess.Id -Force
    if (-not $runningProcess.WaitForExit(10000)) {
        throw "等待监测器退出超时：PID $($runningProcess.Id)"
    }
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path -LiteralPath $runKey) {
    Remove-ItemProperty `
        -LiteralPath $runKey `
        -Name 'CodexCyberMonitor' `
        -ErrorAction SilentlyContinue
}

$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Cyber 实时监测器.lnk'
if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

Set-Location -LiteralPath $env:TEMP
if (Test-Path -LiteralPath $installDirectory -PathType Container) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

if ($RemoveData) {
    if (Test-Path -LiteralPath $dataRoot -PathType Container) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
}

[pscustomobject]@{
    Uninstalled = $true
    DataRemoved = [bool]$RemoveData
}
