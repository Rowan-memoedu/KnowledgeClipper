[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'; $ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$directory=Join-Path $ProjectRoot 'contracts\core\v2.7\checkpoints\v2.7.14-20260901T015000Z';$checkpoint=Join-Path $directory 'checkpoint.json';$output=Join-Path $directory 'invalidation-receipt.v1.json'
function Sha([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
if(!(Test-Path $checkpoint) -or (Test-Path $output)){throw 'Core v2.7.14 invalidation precondition failed.'}
$doc=[ordered]@{schemaVersion='knowledge-clipper-core-checkpoint-invalidation.v1';generatedAt=[DateTimeOffset]::UtcNow.ToString('o');status='invalidated-never-authority-bound';checkpoint=[ordered]@{path=$checkpoint;sha256=Sha $checkpoint};reason='Content pair-integrity label remained 0.9.2 and Anki metadata regressed to 1.3.1 while the effective authority worker is 1.3.2.';authorityBound=$false;externalCalls=0;frozenV1Modified=$false}
[IO.File]::WriteAllText($output,(($doc|ConvertTo-Json -Depth 16)+"`n"),[Text.UTF8Encoding]::new($false));[ordered]@{status=$doc.status;receipt=$output;sha256=Sha $output}|ConvertTo-Json
