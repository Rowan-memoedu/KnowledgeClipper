[CmdletBinding()]
param(
    [switch]$Start,
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe',
    [string]$BuildOutput = 'D:\Artifacts\KnowledgeClipper\build',
    [string]$BuildCache = 'D:\Caches\KnowledgeClipper\build',
    [switch]$InstallShortcut
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$BuildOutput = [IO.Path]::GetFullPath($BuildOutput)
$BuildCache = [IO.Path]::GetFullPath($BuildCache)
$clipperExecutable = Join-Path $BuildOutput 'RemNoteQuickClipper.Desktop\Release\net8.0-windows\win-x64\KnowledgeClipper.exe'
$running = Get-Process -Name KnowledgeClipper -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $clipperExecutable }
if ($running) { throw "构建目标正在使用中：$clipperExecutable。请改用 -BuildOutput 和 -BuildCache，或退出该运行版后再构建。" }
if (-not (Test-Path -LiteralPath $DotnetPath)) { throw '请用 -DotnetPath 指定 .NET 8 SDK 的 dotnet.exe。' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'helpers\node_modules\selection-hook\package.json')) -or
    -not (Test-Path -LiteralPath (Join-Path $projectRoot 'helpers\node_modules\@resvg\resvg-js\package.json'))) {
    & npm ci --prefix (Join-Path $projectRoot 'helpers') --cache 'D:\Caches\npm-cache' --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$env:NUGET_PACKAGES = 'D:\Caches\NuGet\packages'
$chatRoot = Join-Path $projectRoot 'web\chat'
& npm ci --prefix $chatRoot --cache 'D:\Caches\npm-cache' --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$env:KC_CHAT_OUTPUT = Join-Path $BuildOutput 'chat-view'
& npm run build --prefix $chatRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $DotnetPath build (Join-Path $projectRoot 'desktop\RemNoteQuickClipper.Desktop.csproj') -c Release --nologo "-p:KnowledgeClipperBuildOutput=$BuildOutput" "-p:KnowledgeClipperBuildCache=$BuildCache" "-p:KnowledgeClipperChatOutput=$env:KC_CHAT_OUTPUT"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($InstallShortcut -or $BuildOutput -eq 'D:\Artifacts\KnowledgeClipper\build') {
    & (Join-Path $projectRoot 'tools\Install-DesktopShortcut.ps1') -ExecutablePath $clipperExecutable
}
Write-Output $clipperExecutable
if ($Start) { Start-Process -FilePath $clipperExecutable -WindowStyle Normal }
