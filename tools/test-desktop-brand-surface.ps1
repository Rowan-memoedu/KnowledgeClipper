param([Parameter(Mandatory=$true)][string]$BuildId)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$package = Join-Path $project (Join-Path 'dist\desktop-win-x64' $BuildId)
$exe = Join-Path $package 'KnowledgeClipper.exe'
$dll = Join-Path $package 'KnowledgeClipper.dll'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Canonical KnowledgeClipper entrypoint or assembly is missing.' }
$legacyNamedFiles = @(Get-ChildItem -LiteralPath $package -File | Where-Object Name -Like 'RemNoteQuickClipper*')
if ($legacyNamedFiles.Count -ne 0) { throw 'New package exposes legacy product-named files.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo
if ($version.ProductName -ne '零碎知识剪藏' -or $version.FileDescription -ne '零碎知识剪藏') { throw 'Windows product metadata does not use the locked Chinese display name.' }
foreach ($value in @($version.InternalName, $version.OriginalFilename)) {
    if ($value -and $value.Contains('RemNoteQuickClipper', [StringComparison]::OrdinalIgnoreCase)) { throw 'Windows file metadata exposes the legacy product identity.' }
}
$projectText = Get-Content -LiteralPath (Join-Path $project 'desktop\RemNoteQuickClipper.Desktop.csproj') -Raw
if (-not $projectText.Contains('<AssemblyName>KnowledgeClipper</AssemblyName>') -or -not $projectText.Contains('<RootNamespace>KnowledgeClipper</RootNamespace>')) { throw 'Canonical assembly or namespace identity is missing.' }
$desktopUi = Get-Content -LiteralPath (Join-Path $project 'src\DesktopUi.cs') -Raw
if ($desktopUi.Contains('RemNote 工具') -or $desktopUi.Contains('RemNote 剪藏') -or $desktopUi.Contains('RemNoteQuickClipper')) { throw 'Floating desktop surface exposes a legacy product identity.' }
$programUi = Get-Content -LiteralPath (Join-Path $project 'src\Program.cs') -Raw
foreach ($forbidden in @('RemNote Quick Clipper', 'RemNoteQuickClipper', 'RemNote 工具', 'RemNote 摘读设置', '检查 RemNote 连接')) { if ($programUi.Contains($forbidden)) { throw "Settings or tray surface exposes forbidden branding: $forbidden" } }
$startupBody = [regex]::Match($programUi, 'internal static class Program[\s\S]*?internal enum ReviewChoice').Value
if ([regex]::IsMatch($startupBody, '(?m)^\s*JobStore\.PurgeExpired\s*\(')) { throw 'Production startup still invokes the unsafe legacy time-based retention deleter.' }
$settingsUi = Get-Content -LiteralPath (Join-Path $project 'src\ProjectSettingsForm.cs') -Raw
if (-not $settingsUi.Contains('RemNote　　Anki') -or -not $settingsUi.Contains('旧按目录时间删除器已在生产启动禁用')) { throw 'Settings surface lacks peer exporter branding or retention fail-safe disclosure.' }
$inputCanary = Get-Content -LiteralPath (Join-Path $project 'src\InputCanary.cs') -Raw
if (-not $inputCanary.Contains('namespace KnowledgeClipper') -or $inputCanary.Contains('namespace RemNoteQuickClipper') -or $inputCanary.Contains('REMNOTE_QUICK_CLIPPER_CTRL_C_CANARY')) { throw 'Input Canary still exposes the legacy desktop identity.' }
$config = Get-Content -LiteralPath (Join-Path $package 'appsettings.json') -Raw | ConvertFrom-Json
if ($config.WorkDirectory -ne '%LOCALAPPDATA%\KnowledgeClipper') { throw 'New app-data identity is not KnowledgeClipper.' }
if (-not $config.LegacyCompatibilityRuntimePathNote -or -not $config.LegacyCompatibilityRuntimePathNote.Contains('legacy compatibility')) { throw 'Legacy runtime path is not explicitly labeled as a compatibility dependency.' }
Write-Output 'DESKTOP_BRAND_SURFACE_CANARY PASS product=零碎知识剪藏 assembly=KnowledgeClipper entrypoint=KnowledgeClipper.exe'
