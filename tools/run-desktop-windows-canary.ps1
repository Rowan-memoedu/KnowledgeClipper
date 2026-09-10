param([string]$CanaryId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$root = Join-Path $project (Join-Path 'canary\desktop-windows' $CanaryId)
if (Test-Path -LiteralPath $root) { throw "Immutable Canary already exists: $root" }
New-Item -ItemType Directory -Path $root | Out-Null
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'

$uiLog = Join-Path $root 'windows-ui-automation.log'
& $dotnet run --project (Join-Path $project 'desktop\RemNoteQuickClipper.Desktop.WindowsCanary.csproj') --configuration Release 2>&1 | Tee-Object -LiteralPath $uiLog
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$inputLog = Join-Path $root 'ctrl-c-focused-selection.log'
& $dotnet run --project (Join-Path $project 'desktop\KnowledgeClipper.Desktop.InputCanary.csproj') --configuration Release 2>&1 | Tee-Object -LiteralPath $inputLog
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
$graphics = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero)
try { $dpiX = $graphics.DpiX; $dpiY = $graphics.DpiY } finally { $graphics.Dispose() }
$contractReceipt = Join-Path $project 'contracts\desktop-automation\v1\contract-receipt.json'
$contractReceiptV11 = Join-Path $project 'contracts\desktop-automation\v1.1\contract-receipt.json'
$contractReceiptV12 = Join-Path $project 'contracts\desktop-automation\v1.2\contract-receipt.json'
$contractReceiptV13 = Join-Path $project 'contracts\desktop-automation\v1.3\contract-receipt.json'
$contractReceiptV14 = Join-Path $project 'contracts\desktop-automation\v1.4\contract-receipt.json'
$uiText = Get-Content -LiteralPath $uiLog -Raw
$assertionMatch = [regex]::Match($uiText, 'WINDOWS_UI_CANARY PASS assertions=(\d+)')
if (-not $assertionMatch.Success) { throw 'Windows UI Canary did not report its assertion count.' }
$uiAssertions = [int]$assertionMatch.Groups[1].Value
$receipt = [ordered]@{
    schemaVersion = 'desktop-windows-canary.v1'
    canaryId = $CanaryId
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    machine = [ordered]@{ monitorCount = [System.Windows.Forms.Screen]::AllScreens.Count; virtualScreen = [ordered]@{ x=$virtual.X; y=$virtual.Y; width=$virtual.Width; height=$virtual.Height }; systemDpi = [ordered]@{ x=$dpiX; y=$dpiY } }
    automatedWindowsUiCanary = [ordered]@{ status='PASS'; assertions=$uiAssertions; log='windows-ui-automation.log'; logSha256=(Get-FileHash $uiLog -Algorithm SHA256).Hash.ToLowerInvariant() }
    realWindowsCanaries = [ordered]@{
        focusedTextBoxCtrlC = [ordered]@{ status='PASS'; clipboardRestored=$true; log='ctrl-c-focused-selection.log'; logSha256=(Get-FileHash $inputLog -Algorithm SHA256).Hash.ToLowerInvariant() }
        externalApplicationSelectionThroughNoActivateFloatingButton = [ordered]@{ status='PENDING'; reason='Requires controlled physical/system interaction in another application.' }
        realImageClipboard = [ordered]@{ status='PENDING'; reason='Provider path is automated with injection; user clipboard was not replaced for this candidate.' }
        screenshotOverlayPhysicalDrag = [ordered]@{ status='PENDING'; reason='Virtual-screen mask and deterministic cancel passed; physical drag was not performed.' }
        crossMonitorDpiDrag = [ordered]@{ status='PENDING'; reason='Requires at least two physical displays with different DPI and a physical drag.' }
        focus = [ordered]@{ status='PARTIAL'; evidence='WS_EX_NOACTIVATE assertion PASS plus focused Ctrl+C PASS; external-app end-to-end remains pending.' }
        globalHotkey = [ordered]@{ status='PARTIAL'; evidence='RegisterHotKey/UnregisterHotKey boundary PASS; no product shortcut mapping was invented.' }
    }
    externalTargetsAndProviders = [ordered]@{ status='NOT_RUN'; exportTargets=[ordered]@{ remnoteInvoked=$false; ankiInvoked=$false }; imageProviderInvoked=$false; physicalSpeakerInvoked=$false }
    desktopContract = [ordered]@{ receiptSha256=(Get-FileHash $contractReceipt -Algorithm SHA256).Hash.ToLowerInvariant(); expectedTreeSha256='d4d0cca8d1210a90f484a6d31643a8312f67ab6f1e53fc2367308af009194a6e' }
    desktopContractV11 = [ordered]@{ receiptSha256=(Get-FileHash $contractReceiptV11 -Algorithm SHA256).Hash.ToLowerInvariant(); treeSha256=(Get-Content -LiteralPath $contractReceiptV11 -Raw | ConvertFrom-Json).treeSha256; version='1.1.0' }
    desktopContractV12 = [ordered]@{ receiptSha256=(Get-FileHash $contractReceiptV12 -Algorithm SHA256).Hash.ToLowerInvariant(); treeSha256=(Get-Content -LiteralPath $contractReceiptV12 -Raw | ConvertFrom-Json).treeSha256; version='1.2.0' }
    desktopContractV13 = [ordered]@{ receiptSha256=(Get-FileHash $contractReceiptV13 -Algorithm SHA256).Hash.ToLowerInvariant(); treeSha256=(Get-Content -LiteralPath $contractReceiptV13 -Raw | ConvertFrom-Json).treeSha256; version='1.3.0' }
    desktopContractV14 = [ordered]@{ receiptSha256=(Get-FileHash $contractReceiptV14 -Algorithm SHA256).Hash.ToLowerInvariant(); treeSha256=(Get-Content -LiteralPath $contractReceiptV14 -Raw | ConvertFrom-Json).treeSha256; version='1.4.0' }
}
$receiptPath = Join-Path $root 'receipt.json'
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
Write-Host "DESKTOP_WINDOWS_CANARY RECEIPT: $receiptPath"
