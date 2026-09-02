[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseStamp,
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'New-KnowledgeClipperAuthorityV1215.ps1') -Raw
$replacements = [ordered]@{
    '"[string]`$RegistryVersion = ''1.2.15''"' = '"[string]`$RegistryVersion = ''1.2.16''"'
    '"[string]`$CoreVersion = ''2.6.0''"' = '"[string]`$CoreVersion = ''2.7.0''"'
    '"[string]`$CoreCheckpoint = ''contracts/core/v2.6/checkpoints/v2.6.0-20260830T120900Z/checkpoint.json''"' = '"[string]`$CoreCheckpoint = ''contracts/core/v2.7/checkpoints/v2.7.1-20260830T125000Z/checkpoint.json''"'
    "'releases/knowledge-clipper-authority-v1.2.14-20260830T184700Z/win-x64'" = "'releases/knowledge-clipper-authority-v1.2.15-20260830T121200Z/win-x64'"
    "'contracts/authority/worker-registry/v1.2.14-20260830T184700Z/worker-registry.v1.json'" = "'contracts/authority/worker-registry/v1.2.15-20260830T121200Z/worker-registry.v1.json'"
    "'F913D27758E0AD20859B7C0C2B5B8756B0D8487E346FE0FD9732D3D480C34D65'" = "'B6909AFC256F299E6D9B3128C58B90B99988E412D4481C47AC2671B42440E9E1'"
    "'export.anki' = [ordered]@{ version = '1.3.0'; source = 'releases/knowledge-clipper-anki-exporter-v1.3.0-20260830T113059Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = 'E38ED8ECB84785A555214C855B083DE1F5277225A197ED49254E15A2DE73C3C7'; treeSha256 = '3784600C634987F25A84E319809E831A645311EFDD49D26231002EBC7F7D44A0' }" = "'export.anki' = [ordered]@{ version = '1.3.1'; source = 'releases/knowledge-clipper-anki-exporter-v1.3.1-20260830T123155Z/win-x64/workers/anki'; destination = 'workers/anki'; executable = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'; sha256 = '72F9F1F41A502430FEDCDF6B058C4CE7DC6FF8F092C2BB1888603FCCE79FF5DD'; treeSha256 = '653DABD25F5B17B4FA647739281FD3E29D52E74494FD20242AFBC22456232389' }"
    "'anki-original-media-admission.json' = 'workers/anki-exporter/canary/original-media-v1.3.0/20260830T113121Z/qualification-receipt.json'" = "'anki-initial-alias-admission.json' = 'workers/anki-exporter/canary/sealed-initial-alias-v1.3.1/20260830T123343Z/qualification-receipt.json'"
    "'core-settings.json' = 'canary/core-v2.6.0-offline/20260830T120500Z/settings-control.receipt.json'" = "'core-settings.json' = 'canary/core-v2.7.0-offline/20260830T124700Z/settings-control.receipt.json'"
    "'core-adapter.json' = 'canary/core-v2.6.0-offline/20260830T120500Z/core-adapter.receipt.json'" = "'core-adapter.json' = 'canary/core-v2.7.0-offline/20260830T124700Z/core-adapter.receipt.json'"
    'canary/core-v2.6.0-offline/20260830T120500Z/orchestrator-contract-tests.receipt.json' = 'canary/core-v2.7.0-offline/20260830T124700Z/orchestrator-contract-tests.receipt.json'
    'canary/core-v2.6.0-offline/20260830T120500Z/question-workflow.receipt.json' = 'canary/core-v2.7.0-offline/20260830T124700Z/question-workflow.receipt.json'
    'canary/core-v2.6.0-offline/20260830T120500Z/public-contracts.receipt.json' = 'canary/core-v2.7.0-offline/20260830T124700Z/public-contracts.receipt.json'
    'ReplaceDirectory (Join-Path `$ProjectRoot ''contracts/core/v2.6'') (Join-Path `$stagingRelease ''contracts/core/v2.6'')"' = 'ReplaceDirectory (Join-Path `$ProjectRoot ''contracts/core/v2.6'') (Join-Path `$stagingRelease ''contracts/core/v2.6'')`r`n    ReplaceDirectory (Join-Path `$ProjectRoot ''contracts/core/v2.7'') (Join-Path `$stagingRelease ''contracts/core/v2.7'')"'
    "baseAuthority = '1.2.14'; supersedesCheckpointSha256 = 'C2DD2E6C2099E9D7A2D4C742A53BEDE28EA229CEF50D849A464F7C20314121D4'" = "baseAuthority = '1.2.15'; supersedesCheckpointSha256 = 'B5F1EF63EF812D86C360D0AFB46ECDFB220C96C24CD6CF2252E18411D82084A1'"
    "speechSettings = 'language-override-bound-discovery-persistence-conditional'; ankiProbe = 'explicit-read-only'; remnoteProbe = 'public-readonly-module-admitted-core-dispatch-conditional'" = "speechSettings = 'language-override-bound-public-voice-catalog-and-explicit-preview'; ankiProbe = 'explicit-read-only'; remnoteProbe = 'public-readonly-dispatch'"
}
foreach ($pair in $replacements.GetEnumerator()) {
    if (-not $template.Contains($pair.Key, [StringComparison]::Ordinal)) { throw "Authority wrapper marker is missing: $($pair.Key)" }
    $template = $template.Replace($pair.Key, $pair.Value, [StringComparison]::Ordinal)
}

$markerMatch = [regex]::Match($template, '\}\r?\nforeach \(\$pair in \$replacements\.GetEnumerator\(\)\) \{')
if (-not $markerMatch.Success) { throw 'Authority wrapper replacement-map terminator is missing.' }
$marker = $markerMatch.Value
$additional = @'
    "foreach (`$relative in @('contracts/remnote-readonly-probe-request.v1.schema.json'" = "foreach (`$relative in @('contracts/v1/schemas/common/artifact.schema.json','content-worker/schemas/language-practice-decision.v1.schema.json','contracts/remnote-readonly-probe-request.v1.schema.json'"
    "`$releaseTree = Tree `$stagingRelease" = "`$schemaClosureText = ((& python (Join-Path `$PSScriptRoot 'validate_release_schema_closure.py') --release-root `$stagingRelease 2>&1) -join [Environment]::NewLine)`r`n    try { `$schemaClosure = `$schemaClosureText | ConvertFrom-Json -Depth 100 } catch { throw `"Portable recursive schema closure gate returned invalid JSON: `$schemaClosureText`" }`r`n    if (`$LASTEXITCODE -ne 0 -or `$schemaClosure.status -ne 'passed') { throw `"Portable recursive schema closure gate failed: `$schemaClosureText`" }`r`n    `$schemaClosurePath = Join-Path `$stagingAuthority 'portable-schema-closure-gate.v1.json'`r`n    WriteJson `$schemaClosurePath `$schemaClosure`r`n    `$releaseTree = Tree `$stagingRelease"
    "treeHashAlgorithm = 'sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8'" = "treeHashAlgorithm = 'sort relative paths ordinal; path TAB bytes TAB lowercase SHA-256; LF; SHA-256 UTF-8'`r`n        portableSchemaClosure = [ordered]@{ path=(Join-Path `$authorityRoot 'portable-schema-closure-gate.v1.json'); sha256=Sha `$schemaClosurePath; schemasChecked=`$schemaClosure.schemasChecked; localRefsChecked=`$schemaClosure.localRefsChecked }"
    "`$finalDependency = Join-Path `$authorityRoot 'dependency-manifest.v1.json'" = "`$finalDependency = Join-Path `$authorityRoot 'dependency-manifest.v1.json'`r`n    `$finalSchemaClosure = Join-Path `$authorityRoot 'portable-schema-closure-gate.v1.json'`r`n    `$finalSchemaText = ((& python (Join-Path `$PSScriptRoot 'validate_release_schema_closure.py') --release-root `$releaseRoot 2>&1) -join [Environment]::NewLine)`r`n    `$finalSchema = `$finalSchemaText | ConvertFrom-Json -Depth 100`r`n    if (`$LASTEXITCODE -ne 0 -or `$finalSchema.status -ne 'passed' -or `$finalSchema.schemasChecked -ne `$schemaClosure.schemasChecked -or `$finalSchema.localRefsChecked -ne `$schemaClosure.localRefsChecked) { throw 'Final portable recursive schema closure gate failed.' }"
    "dependencyManifestSha256 = Sha `$finalDependency }" = "dependencyManifestSha256 = Sha `$finalDependency; portableSchemaClosure=[ordered]@{ path=`$finalSchemaClosure; sha256=Sha `$finalSchemaClosure; schemasChecked=`$finalSchema.schemasChecked; localRefsChecked=`$finalSchema.localRefsChecked } }"
    "dependencyManifest = [ordered]@{ path = `$finalDependency; sha256 = Sha `$finalDependency }; assemblyReceipt" = "dependencyManifest = [ordered]@{ path = `$finalDependency; sha256 = Sha `$finalDependency }; portableSchemaClosure = [ordered]@{ path=`$finalSchemaClosure; sha256=Sha `$finalSchemaClosure; schemasChecked=`$finalSchema.schemasChecked; localRefsChecked=`$finalSchema.localRefsChecked }; assemblyReceipt"
}
foreach ($pair in $replacements.GetEnumerator()) {
'@
$template = $template.Replace($marker, $additional, [StringComparison]::Ordinal)

$temporary = Join-Path $PSScriptRoot ('.New-KnowledgeClipperAuthorityV1216.' + [Guid]::NewGuid().ToString('N') + '.tmp.ps1')
try {
    [IO.File]::WriteAllText($temporary, $template, [Text.UTF8Encoding]::new($false))
    & $temporary -ReleaseStamp $ReleaseStamp -ProjectRoot $ProjectRoot
    if ($LASTEXITCODE -ne 0) { throw "Authority v1.2.16 assembly failed with exit code $LASTEXITCODE." }
}
finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { [IO.File]::Delete($temporary) } }
