[CmdletBinding()]
param(
    [switch]$SkipTests
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

    $expectedParentItem = Get-Item -LiteralPath $fullExpectedParent -Force
    if (-not $expectedParentItem.PSIsContainer -or
        ($expectedParentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "递归清理的预期父目录不是普通本地目录：$fullExpectedParent"
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

$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$project = Join-Path $root 'src\CodexCyberMonitor\CodexCyberMonitor.csproj'
$iconPath = Join-Path $root 'src\CodexCyberMonitor\Assets\CodexCyberMonitor.ico'
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist\win-x64'))
$release = [IO.Path]::GetFullPath((Join-Path $root 'release'))

foreach ($target in @($dist, $release)) {
    if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理仓库外目录：$target"
    }
}

if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    & (Join-Path $root 'tools\New-AppIcon.ps1') -OutputPath $iconPath | Out-Host
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "应用图标生成后仍不存在：$iconPath"
}

if (Test-Path -LiteralPath $dist) {
    Assert-SafeRecursiveRemovalTarget `
        -TargetPath $dist `
        -ExpectedParentPath ([IO.Path]::GetDirectoryName($dist))
    Remove-Item -LiteralPath $dist -Recurse -Force
}
if (Test-Path -LiteralPath $release) {
    Assert-SafeRecursiveRemovalTarget -TargetPath $release -ExpectedParentPath $root
    Remove-Item -LiteralPath $release -Recurse -Force
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path $release -Force | Out-Null

& dotnet restore $project -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore 失败，退出码：$LASTEXITCODE"
}

& dotnet build $project -c Release -r win-x64 --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build 失败，退出码：$LASTEXITCODE"
}

& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -o $dist `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

$exe = Join-Path $dist 'CodexCyberMonitor.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "发布完成后未找到可执行文件：$exe"
}

if (-not $SkipTests) {
    $selfTestProcess = Start-Process `
        -FilePath $exe `
        -ArgumentList '--self-test' `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($selfTestProcess.ExitCode -ne 0) {
        throw "发布后 EXE 内置自检失败，退出码：$($selfTestProcess.ExitCode)"
    }
}

Copy-Item -LiteralPath (Join-Path $root 'packaging\安装并启动.ps1') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\卸载.ps1') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root '成品使用说明.md') -Destination $dist -Force

$hash = Get-FileHash -LiteralPath $exe -Algorithm SHA256
$hashLine = "{0}  CodexCyberMonitor.exe" -f $hash.Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $dist 'SHA256.txt') -Value $hashLine -Encoding UTF8

$zipPath = Join-Path $release 'CodexCyberMonitor-win-x64-1.1.0.zip'
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zipPath -CompressionLevel Optimal

[pscustomobject]@{
    Executable = $exe
    SizeMB = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 2)
    SHA256 = $hash.Hash.ToLowerInvariant()
    Package = $zipPath
}
