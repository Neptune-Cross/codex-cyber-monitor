[CmdletBinding()]
param(
    [switch]$NoAutoStart,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparsePointTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RootPath
    )

    if (-not (Test-Path -LiteralPath $RootPath)) {
        return
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push([IO.Path]::GetFullPath($RootPath))
    while ($pendingDirectories.Count -gt 0) {
        $currentPath = $pendingDirectories.Pop()
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        if (-not $currentItem.PSIsContainer) {
            throw "预期目录实际为非目录对象：$currentPath"
        }
        if (($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "拒绝在 reparse point/junction 中安装：$currentPath"
        }

        foreach ($child in Get-ChildItem -LiteralPath $currentPath -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "安装目录树中存在 reparse point/junction，拒绝覆盖：$($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pendingDirectories.Push($child.FullName)
            }
        }
    }
}

$sourceExe = Join-Path $PSScriptRoot 'CodexCyberMonitor.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw "安装包中缺少 CodexCyberMonitor.exe：$sourceExe"
}
$sourceExeItem = Get-Item -LiteralPath $sourceExe -Force
if (($sourceExeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "安装包中的 CodexCyberMonitor.exe 是 reparse point/symbolic link，拒绝安装：$sourceExe"
}

$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsRoot 'CodexCyberMonitor'))
if (-not [string]::Equals(
    [IO.Path]::GetDirectoryName($installDirectory),
    $programsRoot,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "安装目录校验失败：$installDirectory"
}

$localAppDataRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
if (-not [string]::Equals(
    [IO.Path]::GetDirectoryName($programsRoot),
    $localAppDataRoot,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Programs 目录不在预期的 LOCALAPPDATA 根目录下：$programsRoot"
}
if (-not (Test-Path -LiteralPath $programsRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $programsRoot -Force | Out-Null
}
$programsRootItem = Get-Item -LiteralPath $programsRoot -Force
if (($programsRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Programs 目录是 reparse point/junction，拒绝安装：$programsRoot"
}

if (-not (Test-Path -LiteralPath $installDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
}
Assert-NoReparsePointTree -RootPath $installDirectory

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
        throw "等待旧版监测器退出超时：PID $($runningProcess.Id)"
    }
}
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '卸载.ps1') -Destination $installDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '成品使用说明.md') -Destination $installDirectory -Force

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoAutoStart) {
    if (-not (Test-Path -LiteralPath $runKey)) {
        New-Item -Path $runKey -Force | Out-Null
    }
    $runCommand = '"{0}" --background' -f $targetExe
    New-ItemProperty `
        -LiteralPath $runKey `
        -Name 'CodexCyberMonitor' `
        -PropertyType String `
        -Value $runCommand `
        -Force | Out-Null
}

$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenuDirectory 'Codex Cyber 实时监测器.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetExe
$shortcut.Arguments = '--show'
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$targetExe,0"
$shortcut.Description = '实时监测 Codex 结构化 Cyber 事件'
$shortcut.Save()

if (-not $NoLaunch) {
    Start-Process -FilePath $targetExe -ArgumentList '--background' -WindowStyle Hidden
}

[pscustomobject]@{
    Installed = $true
    Executable = $targetExe
    AutoStart = (-not $NoAutoStart)
    Running = (-not $NoLaunch)
    Shortcut = $shortcutPath
}
