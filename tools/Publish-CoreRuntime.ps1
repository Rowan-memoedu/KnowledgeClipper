[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $projectRoot 'orchestrator\KnowledgeClipper.Orchestrator.csproj'
$output = Join-Path $projectRoot "dist\orchestrator\win-x64\v$Version"
$dotnet = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Required SDK is missing: $dotnet" }
if (Test-Path -LiteralPath $output) { throw "Append-only Core runtime target exists: $output" }
& $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=$Version -o $output
if ($LASTEXITCODE -ne 0) { throw "Core runtime publish failed with exit code $LASTEXITCODE." }
$executable = Join-Path $output 'KnowledgeClipper.Orchestrator.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Published Core executable is missing.' }
[ordered]@{
    status = 'passed'
    version = $Version
    executable = $executable
    bytes = (Get-Item -LiteralPath $executable).Length
    sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
} | ConvertTo-Json
