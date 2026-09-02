[CmdletBinding()]
param(
    [string]$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-original-anki-v1.2.16\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AuthorityV1215OriginalAnkiCanary.ps1') -Raw
$replacements = [ordered]@{
    'releases\knowledge-clipper-authority-v1.2.15-20260830T121200Z\win-x64' = 'releases\knowledge-clipper-authority-v1.2.16-20260830T125200Z\win-x64'
    'contracts\authority\worker-registry\v1.2.15-20260830T121200Z' = 'contracts\authority\worker-registry\v1.2.16-20260830T125200Z'
    "[string]`$EvidenceRoot = ('C:\Users\fy\Desktop\新建文件夹\04 Projects\RemNote Quick Clipper\canary\sealed-original-anki-v1.2.15\{0}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))" = "[string]`$EvidenceRoot = '" + $EvidenceRoot.Replace("'", "''") + "'"
    '0ea0131dda8a13667fbd9f758e05c27c8c8aca7ed982939ccbe6e042d8a35830' = '42250cc251eea1bebe2cb4e072e19c73f7cf25417010b5c33c922755ea46e757'
    'b5f1ef63ef812d86c360d0afb46ecdfb220c96c24cd6cf2252e18411d82084a1' = '48d6e25e967f73f1d0489df1a6ba8c8f93212b5fad1fad832c5c09a3d58ac046'
    '1ee66639d12c0fa9ebe809117c7bc02789a562c3a1063cbcae874a073c38524a' = '1548acb0f950e6103c89635e2698004685bc8b709d67d36221d22e6951385db0'
    'E5067C1B93A47B5650A2B1A049F974A73F66E31C3A5CEFC5DBD4D33B623190D4' = 'D4223A17AAF308A0A30214526B368E3411D5254831EF96EC820BE268A77B43EF'
    'v1.2.15' = 'v1.2.16'
    'v1215' = 'v1216'
    "worker='export.anki@1.3.0'" = "worker='export.anki@1.3.1'"
    'Core v2.6.0 Orchestrator drifted.' = 'Core v2.7.0 Orchestrator drifted.'
    '$config.provision_managed = $false' = '$config.provision_managed = $true'
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Canary wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}
$template = $template.Replace('V1215', 'V1216', [StringComparison]::Ordinal)
$temporary = Join-Path $PSScriptRoot ('.Invoke-AuthorityV1216OriginalAnkiCanary.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Authority v1.2.16 original Anki Canary failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
