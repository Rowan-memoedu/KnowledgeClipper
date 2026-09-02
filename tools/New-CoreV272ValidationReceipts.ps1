[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-CoreV271ValidationReceipts.ps1') -Raw
if (-not $template.Contains('[string]`$RuntimeVersion = ''2.7.1''', [StringComparison]::Ordinal)) {
    throw 'Core v2.7.2 validation wrapper marker is missing.'
}
$template = $template.Replace('[string]`$RuntimeVersion = ''2.7.1''', '[string]`$RuntimeVersion = ''2.7.2''', [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.New-CoreV272ValidationReceipts.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -OutputDirectory $OutputDirectory -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Core v2.7.2 validation failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
