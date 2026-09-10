[CmdletBinding()]
param(
    [switch]$Start,
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not (Test-Path -LiteralPath $DotnetPath)) { throw '请用 -DotnetPath 指定 .NET 8 SDK 的 dotnet.exe。' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'helpers\node_modules\selection-hook\package.json'))) {
    & npm ci --prefix (Join-Path $projectRoot 'helpers') --cache 'D:\Caches\npm-cache' --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$env:NUGET_PACKAGES = 'D:\Caches\NuGet\packages'
& $DotnetPath build (Join-Path $projectRoot 'desktop\RemNoteQuickClipper.Desktop.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$clipperExecutable = 'D:\Artifacts\KnowledgeClipper\build\RemNoteQuickClipper.Desktop\Release\net8.0-windows\win-x64\KnowledgeClipper.exe'
Write-Output $clipperExecutable
if ($Start) { Start-Process -FilePath $clipperExecutable -WindowStyle Normal }
