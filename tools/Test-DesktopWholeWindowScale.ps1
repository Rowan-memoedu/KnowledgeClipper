#Requires -Version 7.0
param(
    [string]$EvidenceRoot = ('D:\Artifacts\KnowledgeClipper\desktop-scaling\scale-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')),
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'immutable-scale-evidence-exists' }
$null = New-Item -ItemType Directory -Path $EvidenceRoot
$logPath = Join-Path $EvidenceRoot 'scale.log'
& $DotnetPath run --project (Join-Path $repo 'desktop\RemNoteQuickClipper.Desktop.WindowsCanary.csproj') --configuration Release -- --content-scale-only (Join-Path $EvidenceRoot 'windows') 2>&1 | Tee-Object -FilePath $logPath
$testExit = $LASTEXITCODE
$match = [regex]::Match((Get-Content -LiteralPath $logPath -Raw), 'WINDOWS_CONTENT_SCALE_CANARY PASS assertions=(\d+)')
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-desktop-whole-window-scale-test.v1'
    status = $(if ($testExit -eq 0 -and $match.Success) { 'PASS' } else { 'FAIL' })
    assertions = $(if ($match.Success) { [int]$match.Groups[1].Value } else { 0 })
    exitCode = $testExit
    testedScales = @(1.0, 1.5, 2.0, 1.25, 1.0)
    mechanism = 'real WinForms window; programmatic ClientSize; control/font/layout assertions'
    sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'src\DesktopUi.cs')).Hash
    testSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $repo 'src\WindowsUiCanary.cs')).Hash
    logSha256 = (Get-FileHash -LiteralPath $logPath).Hash
    previews = @(Get-ChildItem -LiteralPath (Join-Path $EvidenceRoot 'windows') -Filter '*.png' -File | ForEach-Object { [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } })
    boundaries = @('physical repeated drag is not this test', 'mixed-monitor DPI requires physical verification', 'external providers not invoked', 'existing user processes not terminated')
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'receipt.json') -Encoding utf8NoBOM
Write-Output ('SCALE_RECEIPT ' + (Join-Path $EvidenceRoot 'receipt.json'))
if ($receipt.status -ne 'PASS') { exit 1 }
