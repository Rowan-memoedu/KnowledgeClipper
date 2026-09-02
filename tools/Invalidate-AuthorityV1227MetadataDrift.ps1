[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'; $ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$directory=Join-Path $ProjectRoot 'contracts\authority\worker-registry\v1.2.27-20260901T020000Z';$checkpoint=Join-Path $directory 'checkpoint.json';$output=Join-Path $directory 'invalidation-receipt.v1.json'
function Sha([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
if(!(Test-Path $checkpoint) -or (Test-Path $output)){throw 'Authority v1.2.27 invalidation precondition failed.'}
$doc=[ordered]@{schemaVersion='knowledge-clipper-authority-invalidation-receipt.v1';generatedAt=[DateTimeOffset]::UtcNow.ToString('o');status='invalidated-never-promoted';authority=[ordered]@{registryVersion='1.2.27';checkpoint=$checkpoint;checkpointSha256=Sha $checkpoint};reason='Bound Core v2.7.14 metadata had stale Content 0.9.2 pair-integrity wording and Anki 1.3.1 metadata; no task was dispatched.';externalWorkerRuns=0;externalWrites=0;frozenV1Modified=$false}
[IO.File]::WriteAllText($output,(($doc|ConvertTo-Json -Depth 16)+"`n"),[Text.UTF8Encoding]::new($false));[ordered]@{status=$doc.status;receipt=$output;sha256=Sha $output}|ConvertTo-Json
