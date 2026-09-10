param(
    [string]$RepositoryRoot = 'D:\Projects\RemNote Quick Clipper',
    [string]$ArtifactsRoot = 'D:\Artifacts\KnowledgeClipper\desktop-flashcard-preview',
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe',
    [string]$QualityMatrix = 'D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260903T115121Z\flashcard-preview-gate-matrix.json'
)

$ErrorActionPreference = 'Stop'

function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Save([string]$Path, [string[]]$Lines) { [IO.File]::WriteAllLines($Path, $Lines, [Text.UTF8Encoding]::new($false)) }
function Run([string]$Name, [scriptblock]$Action) {
    $lines = @(& $Action 2>&1 | ForEach-Object { $_.ToString() })
    $exit = $LASTEXITCODE
    Save (Join-Path $script:EvidenceRoot $Name) $lines
    if ($exit -ne 0) { throw "$Name failed with exit $exit" }
}

$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$EvidenceRoot = Join-Path ([IO.Path]::GetFullPath($ArtifactsRoot)) ("desktop-flashcard-preview-$stamp")
if (Test-Path -LiteralPath $EvidenceRoot) { throw "receipt-root-already-exists: $EvidenceRoot" }
New-Item -ItemType Directory -Path $EvidenceRoot | Out-Null

Push-Location $repo
try {
    Run 'build-application.log' { & $DotnetPath build '.\tests\KnowledgeClipper.Desktop.Application.Tests\KnowledgeClipper.Desktop.Application.Tests.csproj' -c Release --no-restore --nologo }
    Run 'build-gateway.log' { & $DotnetPath build '.\tests\KnowledgeClipper.Desktop.CoreGateway.Tests\KnowledgeClipper.Desktop.CoreGateway.Tests.csproj' -c Release --no-restore --nologo }
    Run 'build-windows-ui.log' { & $DotnetPath build '.\desktop\RemNoteQuickClipper.Desktop.WindowsCanary.csproj' -c Release --no-restore --nologo }
    Run 'build-production.log' { & $DotnetPath build '.\desktop\RemNoteQuickClipper.Desktop.csproj' -c Release --no-restore --nologo }
    Run 'application-tests.log' { & $DotnetPath '.\tests\KnowledgeClipper.Desktop.Application.Tests\bin\Release\net8.0\KnowledgeClipper.Desktop.Application.Tests.dll' }
    Run 'gateway-tests.log' { & $DotnetPath '.\tests\KnowledgeClipper.Desktop.CoreGateway.Tests\bin\Release\net8.0\KnowledgeClipper.Desktop.CoreGateway.Tests.dll' }
    Run 'windows-flashcard-preview-ui.log' { & '.\desktop\bin\RemNoteQuickClipper.Desktop.WindowsCanary\Release\net8.0-windows\win-x64\KnowledgeClipper.Desktop.WindowsCanary.exe' --flashcard-preview-only }
}
finally { Pop-Location }

$sources = @(
    'src/KnowledgeClipper.Desktop.Application/FlashcardDraftPreview.cs',
    'src/FlashcardPreviewUi.cs',
    'src/KnowledgeClipper.Desktop.Adapters/NamedPipeCoreGateway.cs',
    'src/LegacyDesktopAdapter.cs',
    'tests/KnowledgeClipper.Desktop.Application.Tests/Program.cs',
    'src/WindowsUiCanary.cs'
)
$sourceEvidence = foreach ($relative in $sources) {
    $path = Join-Path $repo $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    [ordered]@{ path = $relative; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Sha $path }
}
$logs = foreach ($file in Get-ChildItem -LiteralPath $EvidenceRoot -File | Sort-Object Name) {
    [ordered]@{ path = $file.Name; bytes = $file.Length; sha256 = Sha $file.FullName }
}
$receipt = [ordered]@{
    schemaVersion = 'knowledge-clipper-desktop-flashcard-preview-module-receipt.v1'
    status = 'PASS-DESKTOP-FAKE-BOUNDARY'
    createdAtUtc = [DateTime]::UtcNow.ToString('O')
    scope = 'Desktop domain-neutral flashcard preview reducer and one WinForms component'
    qualityMatrix = [ordered]@{ path = $QualityMatrix; sha256 = Sha $QualityMatrix }
    assertions = [ordered]@{ application = 64; gateway = 21; windowsUi = 8 }
    guarantees = @(
        'all entry sources use one preview reducer and one component identity',
        'ordered literal Front and Back are rendered without UI card construction',
        'before explicit confirm the fake destination command count is zero',
        'confirm carries operationId draftToken revision and digest exactly once',
        'cancel sends zero destination commands',
        'follow-up replaces the revision in the same component and stale projection is rejected',
        'restart reprojection preserves literal cards and does not auto-confirm',
        'production gateway fails closed before Anki configuration or exporter-capable Core submission while typed confirm SDK is unavailable'
    )
    automationIds = @(
        'knowledge-clipper.desktop.flashcard-preview.window',
        'knowledge-clipper.desktop.flashcard-preview.status',
        'knowledge-clipper.desktop.flashcard-preview.cards',
        'knowledge-clipper.desktop.flashcard-preview.front',
        'knowledge-clipper.desktop.flashcard-preview.back',
        'knowledge-clipper.desktop.flashcard-preview.destinations',
        'knowledge-clipper.desktop.flashcard-preview.confirm',
        'knowledge-clipper.desktop.flashcard-preview.cancel',
        'knowledge-clipper.desktop.flashcard-preview.follow-up-input',
        'knowledge-clipper.desktop.flashcard-preview.follow-up'
    )
    pendingCoreBoundary = @(
        'typed draft-only submission and projection shared by read-aloud and question flows',
        'single-use confirm action bound to operation revision token and digest',
        'typed follow-up revision invalidation and restart recovery',
        'typed confirmed Anki semantic readback'
    )
    promotion = [ordered]@{ userHandTestEntrypoint = $false; reason = 'Core typed draft-confirm SDK and candidate-bound Quality seven-gate evidence are pending' }
    sideEffects = [ordered]@{ externalCalls = 0; ankiWrites = 0; remnoteWrites = 0; physicalSpeaker = 0; ocrRuns = 0 }
    sourceFiles = $sourceEvidence
    evidence = $logs
}
$receiptPath = Join-Path $EvidenceRoot 'receipt.json'
[IO.File]::WriteAllText($receiptPath, (($receipt | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output ([ordered]@{ root = $EvidenceRoot; receipt = $receiptPath; receiptSha256 = Sha $receiptPath } | ConvertTo-Json -Compress)
