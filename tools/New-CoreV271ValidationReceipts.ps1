[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV260ValidationReceipts.ps1') -Raw
$template = $template.Replace("[string]`$RuntimeVersion = '2.6.0'", "[string]`$RuntimeVersion = '2.7.1'", [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-CoreV271ValidationReceipts.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -OutputDirectory $OutputDirectory -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.1 validation failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
