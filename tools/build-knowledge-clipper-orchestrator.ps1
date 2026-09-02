[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$legacySdkAlias = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
$dotnet = $legacySdkAlias # Compatibility-only SDK installation path; not a product identity.
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Required .NET 8 SDK is missing: $dotnet"
}

$project = Join-Path $projectRoot 'orchestrator\KnowledgeClipper.Orchestrator.csproj'
$output = Join-Path $projectRoot ("dist\orchestrator\{0}\v{1}" -f $RuntimeIdentifier, $Version)
if (Test-Path -LiteralPath $output) {
    throw "Append-only publish target already exists: $output"
}

& $dotnet publish $project `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$executable = Join-Path $output 'KnowledgeClipper.Orchestrator.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Canonical executable was not published: $executable"
}

[ordered]@{
    schemaVersion = 'knowledge-clipper-build-result.v2'
    productName = '零碎知识剪藏'
    componentId = 'knowledge-clipper.orchestrator'
    version = $Version
    runtimeIdentifier = $RuntimeIdentifier
    output = $output
    executable = $executable
    bytes = (Get-Item -LiteralPath $executable).Length
    sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
} | ConvertTo-Json -Depth 4
