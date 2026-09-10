[CmdletBinding()]
param(
    [ValidateSet('stage','seal')][string]$Mode = 'stage',
    [string]$StagingRuntimeRoot,
    [string]$RepositoryRoot = 'D:\Projects\RemNote Quick Clipper',
    [string]$SourceAuthorityRoot = 'D:\Artifacts\RemNote Quick Clipper\current\knowledge-clipper-authority-v1.2.41-20260902T042000Z\win-x64',
    [string]$ContentReleaseRoot = 'D:\Artifacts\KnowledgeClipper\content-worker\releases\content.deepseek-0.11.0-win-x64-formal-20260904T083500Z',
    [string]$ContentReleaseManifestSha256 = '68101074FD80113BC590EE91A9F741BA158E9FC0F85E8B16F5CDF71B5F20ED99',
    [string]$ContentReleaseTreeSha256 = '78D726DB3BA9DA7C14DCD0C672FCF5C5AD97DE64532650818F24F748AE3EFAC4',
    [string]$AnkiReleaseRoot = 'D:\Artifacts\KnowledgeClipper\anki-exporter\export.anki-v1.4.1-20260903T120000Z\win-x64',
    [string]$PythonRuntimeRoot = 'C:\Users\fy\AppData\Local\Python\pythoncore-3.14-64',
    [string]$EdgeTtsSitePackages = 'D:\Applications\RemNoteQuickClipperRuntime\edge-tts-7.2.8\Lib\site-packages',
    [string]$FfplayPath = 'D:\Applications\P47\FFmpeg\ffmpeg-8.1.2-full_build\bin\ffplay.exe',
    [string]$OutputRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki',
    [string]$StagingOutputRoot = 'D:\Artifacts\KnowledgeClipper\staging\read-to-anki'
)

$ErrorActionPreference = 'Stop'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function HashUtf8([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}
function CopyTree([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Missing required source: $Source" }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}
function CopyContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Missing required source: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force }
}
function Copy-MinimalPythonRuntime([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Missing Python runtime: $Source" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in @('python.exe','python3.dll','python314.dll','vcruntime140.dll','vcruntime140_1.dll','LICENSE.txt')) {
        $file = Join-Path $Source $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination (Join-Path $Destination $name) }
    }
    CopyContents (Join-Path $Source 'DLLs') (Join-Path $Destination 'DLLs')
    $libSource = Join-Path $Source 'Lib'
    $libTarget = Join-Path $Destination 'Lib'
    $files = @(Get-ChildItem -LiteralPath $libSource -Recurse -File -Force | Where-Object {
        $relative = $_.FullName.Substring($libSource.Length + 1).Replace('\','/')
        $relative -notmatch '^(site-packages|ensurepip|idlelib|tkinter|test|tests|turtledemo|venv|unittest|pydoc_data)/' -and $relative -notmatch '(^|/)__pycache__/'
    })
    for ($index = 0; $index -lt $files.Count; $index++) {
        $relative = $files[$index].FullName.Substring($libSource.Length + 1)
        $target = Join-Path $libTarget $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $files[$index].FullName -Destination $target
        if (($index + 1) % 250 -eq 0) { Write-Output "COPY_PYTHON_PROGRESS $($index + 1)/$($files.Count)" }
    }
}
function Get-SchemaReferences([object]$Value, [string]$SchemaPath, [string]$SchemaRoot) {
    $references = @()
    if ($null -eq $Value) { return $references }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string]) -and -not ($Value -is [pscustomobject])) {
        foreach ($item in $Value) { $references += Get-SchemaReferences $item $SchemaPath $SchemaRoot }
        return $references
    }
    if ($Value -is [pscustomobject]) {
        foreach ($property in $Value.PSObject.Properties) {
            if ($property.Name -eq '$ref' -and $property.Value -is [string] -and -not $property.Value.StartsWith('#') -and $property.Value -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
                $relative = $property.Value.Split('#')[0]
                $resolved = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $SchemaPath) $relative))
                $root = [System.IO.Path]::GetFullPath($SchemaRoot).TrimEnd('\') + '\'
                if (-not $resolved.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
                    throw "Portable schema dependency missing or escapes candidate: $SchemaPath ref=$($property.Value) resolved=$resolved"
                }
                $references += [pscustomobject]@{ schema = $SchemaPath.Substring($SchemaRoot.Length + 1).Replace('\','/'); reference = $property.Value; resolvedPath = $resolved.Substring($SchemaRoot.Length + 1).Replace('\','/'); sha256 = Hash $resolved }
            }
            $references += Get-SchemaReferences $property.Value $SchemaPath $SchemaRoot
        }
    }
    return $references
}
function Assert-PortableSchemaClosure([string]$SchemaRoot) {
    $references = @()
    Get-ChildItem -LiteralPath $SchemaRoot -Recurse -File -Filter '*.schema.json' | ForEach-Object {
        $document = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 100
        $references += Get-SchemaReferences $document $_.FullName $SchemaRoot
    }
    return @($references | Sort-Object schema, reference, resolvedPath)
}

if ($Mode -eq 'seal') {
    if ([string]::IsNullOrWhiteSpace($StagingRuntimeRoot) -or -not (Test-Path -LiteralPath $StagingRuntimeRoot -PathType Container)) { throw 'Seal mode requires an existing -StagingRuntimeRoot.' }
    if (Test-Path -LiteralPath (Join-Path $StagingRuntimeRoot 'candidate-manifest.json')) { throw 'Staging runtime must not contain a candidate manifest.' }
    $forbidden = @(Get-ChildItem -LiteralPath $StagingRuntimeRoot -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($StagingRuntimeRoot.Length + 1).Replace('\','/')
        $_.Name -eq 'candidate-manifest.json' -or
        $_.Name -match 'receipt' -or
        $relative -match '^(receipts|evidence|test-host|tests|fixtures|node_modules|src)(/|$)' -or
        $relative -match '^core-v3/(receipts|evidence|test-host|tests|fixtures|node_modules|src)(/|$)'
    })
    if ($forbidden.Count -ne 0) { throw "Staging runtime contains forbidden test/evidence/source content: $($forbidden[0].FullName)" }
    $id = 'rta-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $candidate = Join-Path $OutputRoot $id
    if (Test-Path -LiteralPath $candidate) { throw "Candidate already exists: $candidate" }
    CopyContents $StagingRuntimeRoot $candidate
    $sourceFiles = @(Get-ChildItem -LiteralPath $StagingRuntimeRoot -Recurse -File | Sort-Object { $_.FullName.Substring($StagingRuntimeRoot.Length + 1).Replace('\','/') })
    $files = @()
    for ($index = 0; $index -lt $sourceFiles.Count; $index++) {
        $sourceFile = $sourceFiles[$index]
        $relative = $sourceFile.FullName.Substring($StagingRuntimeRoot.Length + 1)
        $targetFile = Join-Path $candidate $relative
        $sourceHash = Hash $sourceFile.FullName
        $targetHash = Hash $targetFile
        if ($sourceFile.Length -ne (Get-Item -LiteralPath $targetFile).Length -or $sourceHash -ne $targetHash) { throw "Staging-to-candidate copy drifted: $relative" }
        $files += [pscustomobject]@{ path = $relative.Replace('\','/'); bytes = $sourceFile.Length; sha256 = $targetHash }
        if (($index + 1) % 250 -eq 0) { Write-Output "SEAL_HASH_PROGRESS $($index + 1)/$($sourceFiles.Count)" }
    }
    $manifest = [pscustomobject]@{
        schema = 'kc.read-to-anki-candidate-manifest/2'; candidateId = $id; createdAt = (Get-Date).ToUniversalTime().ToString('o');
        stagingSource = [pscustomobject]@{ path = (Resolve-Path -LiteralPath $StagingRuntimeRoot).Path; copiedExactly = $true; files = $sourceFiles.Count; bytes = ($files | Measure-Object bytes -Sum).Sum; mismatches = 0 };
        workers = [pscustomobject]@{ content = 'content.deepseek@0.11.0'; speech = 'speech.edge@1.2.5'; anki = 'export.anki@1.4.1' };
        runtime = [pscustomobject]@{ host = 'core-v3/KnowledgeClipper.Core.V3.Host.exe'; binding = 'contracts/core/v3/capabilities/read-to-anki-capability-binding.v1.json'; toolsDescriptor = 'authority/runtime-tools.v1.json'; selfContained = $true; sdkAndTestsExcluded = $true };
        registryVersion = '1.2.43'; dependencyManifest = 'authority/dependency-manifest.v1.json'; files = $files
    }
    $manifestPath = Join-Path $candidate 'candidate-manifest.json'
    $manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Write-Output "CANDIDATE $candidate"
    Write-Output "MANIFEST_SHA256 $(Hash $manifestPath)"
    exit 0
}

$id = 'rta-staging-' + (Get-Date -Format 'yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$candidate = Join-Path $StagingOutputRoot $id
if (Test-Path -LiteralPath $candidate) { throw "Candidate already exists: $candidate" }
New-Item -ItemType Directory -Path $candidate | Out-Null

# The source authority is read-only. Only the three admitted worker closures and
# their frozen common schema dependencies are copied into this new candidate.
$authority = Join-Path $candidate 'authority\win-x64'
New-Item -ItemType Directory -Path $authority | Out-Null
$contentManifestSource = Join-Path $ContentReleaseRoot 'release-manifest.v1.json'
if (-not (Test-Path -LiteralPath $contentManifestSource -PathType Leaf)) { throw "Content formal release manifest is missing: $contentManifestSource" }
if ((Hash $contentManifestSource) -ne $ContentReleaseManifestSha256) { throw 'Content formal release manifest hash drifted.' }
$contentManifest = Get-Content -LiteralPath $contentManifestSource -Raw | ConvertFrom-Json -Depth 100
if ($contentManifest.worker.workerId -ne 'content.deepseek' -or $contentManifest.worker.workerVersion -ne '0.11.0' -or
    $contentManifest.payloadTreeSha256 -ne $ContentReleaseTreeSha256) { throw 'Content formal release identity or payload tree drifted.' }
$contentTree = [Text.StringBuilder]::new()
foreach ($entry in $contentManifest.files) {
    $relative = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('..') -or [IO.Path]::IsPathRooted($relative)) { throw "Content formal release contains an unsafe path: $relative" }
    $sourceFile = [IO.Path]::GetFullPath((Join-Path $ContentReleaseRoot $relative))
    $releasePrefix = [IO.Path]::GetFullPath($ContentReleaseRoot).TrimEnd('\') + '\'
    if (-not $sourceFile.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Content formal release file is missing or escapes root: $relative" }
    if ((Get-Item -LiteralPath $sourceFile).Length -ne [long]$entry.bytes -or (Hash $sourceFile) -ne [string]$entry.sha256) { throw "Content formal release file drifted: $relative" }
    [void]$contentTree.Append($relative).Append([char]0).Append([long]$entry.bytes).Append([char]0).Append([string]$entry.sha256).Append("`n")
}
if ($contentManifest.files.Count -ne [int]$contentManifest.payloadFileCount -or (HashUtf8 $contentTree.ToString()) -ne $ContentReleaseTreeSha256) { throw 'Content formal release file count or recomputed payload tree drifted.' }
CopyContents $ContentReleaseRoot (Join-Path $authority 'workers\content')
CopyContents (Join-Path $SourceAuthorityRoot 'workers\speech') (Join-Path $authority 'workers\speech')
CopyContents (Join-Path $AnkiReleaseRoot 'workers\anki') (Join-Path $authority 'workers\anki')
# Keep the legacy location used by existing worker closures and the explicit v1
# location required by Core schemas which reference ../../../../v1/schemas/... .
CopyTree (Join-Path $SourceAuthorityRoot 'contracts\v1') (Join-Path $authority 'contracts')
CopyTree (Join-Path $SourceAuthorityRoot 'contracts\v1') (Join-Path $authority 'contracts\v1')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'contracts\authority\worker-registry\v1.2.41-20260902T042000Z\worker-registry.v1.json') -Destination (Join-Path $candidate 'authority\worker-registry.v1.json')
$registryPath = Join-Path $candidate 'authority\worker-registry.v1.json'
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json -Depth 100
$registry.registryVersion = '1.2.43'
$registry | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $registryPath -Encoding utf8NoBOM

# Speech runtime dependencies are part of the sealed candidate. Core admits
# their candidate-relative paths, byte lengths, and hashes before accepting IPC.
$toolsRoot = Join-Path $candidate 'authority\tools'
$edgeRoot = Join-Path $toolsRoot 'edge-tts'
New-Item -ItemType Directory -Path $edgeRoot -Force | Out-Null
Copy-MinimalPythonRuntime $PythonRuntimeRoot (Join-Path $edgeRoot 'python')
CopyContents $EdgeTtsSitePackages (Join-Path $edgeRoot 'site-packages')
$launcherProject = Join-Path $RepositoryRoot 'core-v3\tools\EdgeTtsLauncher\KnowledgeClipper.EdgeTtsLauncher.csproj'
$launcherPublish = Join-Path 'D:\Artifacts\KnowledgeClipper\build' ($id + '-edge-tts-launcher')
& 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe' publish $launcherProject -c Release --nologo -o $launcherPublish
if ($LASTEXITCODE -ne 0) { throw 'Edge TTS launcher publish failed.' }
$edgeLauncher = Join-Path $edgeRoot 'KnowledgeClipper.EdgeTtsLauncher.exe'
Copy-Item -LiteralPath (Join-Path $launcherPublish 'KnowledgeClipper.EdgeTtsLauncher.exe') -Destination $edgeLauncher
$ffplayTarget = Join-Path $toolsRoot 'ffplay\ffplay.exe'
New-Item -ItemType Directory -Path (Split-Path -Parent $ffplayTarget) -Force | Out-Null
Copy-Item -LiteralPath $FfplayPath -Destination $ffplayTarget
$runtimeDescriptorPath = Join-Path $candidate 'authority\runtime-tools.v1.json'
[pscustomobject]@{
    schema = 'kc.authority-runtime-tools/1'; generatedAt = (Get-Date).ToUniversalTime().ToString('o');
    tools = [pscustomobject]@{
        edgeTts = [pscustomobject]@{ path = 'authority/tools/edge-tts/KnowledgeClipper.EdgeTtsLauncher.exe'; bytes = (Get-Item -LiteralPath $edgeLauncher).Length; sha256 = Hash $edgeLauncher; closure = 'authority/tools/edge-tts' }
        ffplay = [pscustomobject]@{ path = 'authority/tools/ffplay/ffplay.exe'; bytes = (Get-Item -LiteralPath $ffplayTarget).Length; sha256 = Hash $ffplayTarget }
    }
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runtimeDescriptorPath -Encoding utf8NoBOM

$core = Join-Path $candidate 'core-v3'
New-Item -ItemType Directory -Path $core | Out-Null
$hostProject = Join-Path $RepositoryRoot 'core-v3\src\KnowledgeClipper.Core.V3.Host\KnowledgeClipper.Core.V3.Host.csproj'
$hostPublish = Join-Path 'D:\Artifacts\KnowledgeClipper\build' ($id + '-core-host')
& 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe' publish $hostProject -c Release -r win-x64 --self-contained true --nologo -o $hostPublish
if ($LASTEXITCODE -ne 0) { throw 'Core Host self-contained publish failed.' }
Get-ChildItem -LiteralPath $hostPublish -File -Force | Where-Object Extension -ne '.pdb' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $core }

$catalogSource = Join-Path $RepositoryRoot 'contracts\core\v3\capabilities\capability-catalog.phase2.v1.json'
$catalog = Get-Content -LiteralPath $catalogSource -Raw | ConvertFrom-Json -Depth 100
$catalog.authorityAnchor.authorityVersion = '1.2.43'
foreach ($operation in $catalog.operations) {
    if ($operation.workerBinding.mode -ne 'worker') { continue }
    $operation.workerBinding.authority.releaseRoot = 'authority/win-x64'
    $operation.workerBinding.authority.registryPath = 'authority/worker-registry.v1.json'
    $operation.workerBinding.authority.registrySha256 = Hash (Join-Path $candidate 'authority\worker-registry.v1.json')
    if ($operation.workerBinding.workerId -eq 'export.anki') {
        $operation.workerBinding.versionRange = '=1.4.1'
        $operation.workerBinding.authority.entrypointPath = 'workers/anki/KnowledgeClipper.AnkiExporter.Worker.exe'
        $operation.workerBinding.authority.entrypointSha256 = Hash (Join-Path $authority 'workers\anki\KnowledgeClipper.AnkiExporter.Worker.exe')
        $operation.workerBinding.authority.exactWorkerVersion = '1.4.1'
        $operation.workerBinding.authority.contractVersion = '1.0.0'
    }
    if ($operation.workerBinding.workerId -eq 'content.deepseek') {
        $operation.workerBinding.versionRange = '=0.11.0'
        $operation.workerBinding.authority.entrypointPath = 'workers/content/KnowledgeClipper.Content.Worker.exe'
        $operation.workerBinding.authority.entrypointSha256 = Hash (Join-Path $authority 'workers\content\KnowledgeClipper.Content.Worker.exe')
        $operation.workerBinding.authority.exactWorkerVersion = '0.11.0'
        $operation.workerBinding.authority.contractVersion = '1.0.0'
    }
}
$speechAuthority = [pscustomobject]@{
    releaseRoot = 'authority/win-x64'; registryPath = 'authority/worker-registry.v1.json';
    registrySha256 = Hash (Join-Path $candidate 'authority\worker-registry.v1.json');
    entrypointPath = 'workers/speech/KnowledgeClipper.Speech.Worker.exe';
    entrypointSha256 = Hash (Join-Path $authority 'workers\speech\KnowledgeClipper.Speech.Worker.exe');
    exactWorkerVersion = '1.2.5'; contractVersion = '1.0.0'
}
$content = $catalog.operations | Where-Object { $_.operationId -eq 'question.initial' } | Select-Object -First 1
$contentAuthority = $content.workerBinding.authority
$catalog.operations += [pscustomobject]@{
    operationId = 'speech.runtime.internal'; available = $true; availabilityReason = $null;
    commandSchema = 'kc.internal-speech-runtime/1'; resultArtifactKind = 'audio.speech'; resultContract = '1.0.0';
    effectClass = 'none'; authorizationPolicy = 'none'; retryPolicy = 'safe'; idempotencyPolicy = 'required'; timeoutMilliseconds = 30000;
    workerBinding = [pscustomobject]@{ mode = 'worker'; workerId = 'speech.edge'; versionRange = '=1.2.5'; handler = $null; authority = $speechAuthority };
    uiAffordance = [pscustomobject]@{ label = 'internal speech runtime'; surface = 'sdk-only'; visible = $false }
}
$catalog.operations += [pscustomobject]@{
    operationId = 'clip.read-aloud-cards'; available = $true; availabilityReason = $null;
    commandSchema = 'kc.read-aloud-cards-command/1'; resultArtifactKind = 'anki-readback'; resultContract = '1.0.0';
    effectClass = 'external-write'; authorizationPolicy = 'explicit'; retryPolicy = 'conditional'; idempotencyPolicy = 'required'; timeoutMilliseconds = 60000;
    workerBinding = [pscustomobject]@{ mode = 'worker'; workerId = 'content.deepseek'; versionRange = $content.workerBinding.versionRange; handler = $null; authority = $contentAuthority };
    uiAffordance = [pscustomobject]@{ label = '朗读并制卡到 Anki'; surface = 'clip'; visible = $true }
}

$bindingDir = Join-Path $candidate 'contracts\core\v3\capabilities'
New-Item -ItemType Directory -Path $bindingDir | Out-Null
$checkpointRelative = 'contracts\core\v2.10\checkpoints\v2.10.6-20260902T041800Z\checkpoint.json'
$checkpointTarget = Join-Path $candidate $checkpointRelative
New-Item -ItemType Directory -Path (Split-Path -Parent $checkpointTarget) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $RepositoryRoot $checkpointRelative) -Destination $checkpointTarget
$catalogPath = Join-Path $bindingDir 'read-to-anki-capability-catalog.v1.json'
$catalog | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $catalogPath -Encoding utf8NoBOM
$binding = [pscustomobject]@{
    schema = 'kc.capability-binding/1'; catalogPath = 'contracts/core/v3/capabilities/read-to-anki-capability-catalog.v1.json';
    catalogSha256 = Hash $catalogPath; authorityAnchor = $catalog.authorityAnchor
}
$bindingPath = Join-Path $bindingDir 'read-to-anki-capability-binding.v1.json'
$binding | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $bindingPath -Encoding utf8NoBOM

$schemaReferences = Assert-PortableSchemaClosure $candidate
$dependencyManifestPath = Join-Path $candidate 'authority\dependency-manifest.v1.json'
[pscustomobject]@{
    schema = 'kc.authority-dependency-manifest/1'; generatedAt = (Get-Date).ToUniversalTime().ToString('o');
    schemaRoot = 'authority/win-x64'; portableRecursiveSchemaValidation = 'passed';
    moduleInputs = [pscustomobject]@{
        content = [pscustomobject]@{
            worker = 'content.deepseek@0.11.0'; copiedPath = 'authority/win-x64/workers/content';
            releaseManifestPath = 'authority/win-x64/workers/content/release-manifest.v1.json';
            releaseManifestSha256 = $ContentReleaseManifestSha256; payloadTreeSha256 = $ContentReleaseTreeSha256;
            entrypointSha256 = Hash (Join-Path $authority 'workers\content\KnowledgeClipper.Content.Worker.exe')
        }
    };
    references = $schemaReferences
} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $dependencyManifestPath -Encoding utf8NoBOM

Write-Output "STAGING_RUNTIME $candidate"
Write-Output "STAGING_FILES $((Get-ChildItem -LiteralPath $candidate -Recurse -File).Count)"
exit 0
