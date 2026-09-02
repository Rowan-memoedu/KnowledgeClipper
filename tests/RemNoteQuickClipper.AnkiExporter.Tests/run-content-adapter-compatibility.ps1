$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$neutralRuntime = 'D:\Applications\KnowledgeClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$legacyCompatibilityRuntime = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $neutralRuntime) {
    $neutralRuntime
} elseif (Test-Path -LiteralPath $legacyCompatibilityRuntime) {
    $legacyCompatibilityRuntime
} elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
    (Get-Command dotnet).Source
} else {
    throw 'Missing .NET 8 runtime. Checked KnowledgeClipper runtime, PATH, and the explicit legacy compatibility runtime.'
}
$testProject = Join-Path $PSScriptRoot 'ContentAdapterCompatibility\ContentAdapterCompatibility.csproj'
$fixtureValidator = Join-Path $project 'content-worker\test\validate_projection_fixtures.py'
python $fixtureValidator
if ($LASTEXITCODE -ne 0) { throw "Content projection fixture validation failed: $LASTEXITCODE" }
& $dotnet build $testProject --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Anki content adapter compatibility build failed: $LASTEXITCODE" }
& $dotnet run --project $testProject --configuration Release --no-build -- $project
if ($LASTEXITCODE -ne 0) { throw "Anki content adapter compatibility acceptance failed: $LASTEXITCODE" }
