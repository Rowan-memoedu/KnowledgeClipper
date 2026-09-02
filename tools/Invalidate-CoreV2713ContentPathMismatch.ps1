[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$directory = Join-Path $ProjectRoot 'contracts\core\v2.7\checkpoints\v2.7.13-20260901T014500Z'
$checkpoint = Join-Path $directory 'checkpoint.json'
$output = Join-Path $directory 'invalidation-receipt.v1.json'
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
if (!(Test-Path -LiteralPath $checkpoint -PathType Leaf) -or (Test-Path -LiteralPath $output)) { throw 'Core v2.7.13 invalidation precondition failed.' }
$document = [ordered]@{schemaVersion='knowledge-clipper-core-checkpoint-invalidation.v1';generatedAt=[DateTimeOffset]::UtcNow.ToString('o');status='invalidated-never-authority-bound';checkpoint=[ordered]@{path=$checkpoint;sha256=Sha $checkpoint};reason='modules.content.executable retained the superseded 0.9.2 path while the describe identity was 0.9.3; superseded by a corrected append-only checkpoint before authority assembly.';authorityBound=$false;externalCalls=0;frozenV1Modified=$false}
[IO.File]::WriteAllText($output, (($document | ConvertTo-Json -Depth 16) + "`n"), [Text.UTF8Encoding]::new($false))
[ordered]@{status=$document.status;receipt=$output;sha256=Sha $output}|ConvertTo-Json -Depth 10
