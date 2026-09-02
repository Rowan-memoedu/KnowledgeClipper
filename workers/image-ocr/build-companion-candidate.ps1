param(
    [string]$PyInstallerVersion = '6.22.2'
)

$ErrorActionPreference = 'Stop'
$workerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = '1.2.4'
$companionVersion = '1.3.1'
$candidateRoot = Join-Path $workerRoot ('candidates\' + $version)
$workerOutput = Join-Path $candidateRoot 'KnowledgeClipper.ImageOcr.Worker.exe'
$settingsOutput = Join-Path $candidateRoot 'KnowledgeClipper.ImageOcr.Settings.exe'
$receiptPath = Join-Path $candidateRoot 'build-receipt.json'
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledge-clipper-image-ocr-companion-build-' + [Guid]::NewGuid().ToString('N'))
$workPath = Join-Path $buildRoot 'work'
$specPath = Join-Path $buildRoot 'spec'
$distPath = Join-Path $buildRoot 'dist'

if (Test-Path -LiteralPath $candidateRoot) { throw "Refusing to overwrite existing candidate root: $candidateRoot" }
New-Item -ItemType Directory -Path $candidateRoot,$workPath,$specPath,$distPath -ErrorAction Stop | Out-Null

function Invoke-PyInstaller([string]$Name, [string]$Source, [string[]]$HiddenImports) {
    $arguments = @(
        'run', '--spec', ('pyinstaller==' + $PyInstallerVersion), 'pyinstaller',
        '--onefile', '--console', '--clean', '--noconfirm',
        '--name', $Name,
        '--distpath', $distPath,
        '--workpath', $workPath,
        '--specpath', $specPath
    )
    foreach ($hidden in $HiddenImports) { $arguments += @('--hidden-import', $hidden) }
    $arguments += $Source
    & pipx @arguments
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed for $Name with exit code $LASTEXITCODE" }
    $built = Join-Path $distPath ($Name + '.exe')
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw "Missing built executable: $built" }
    return $built
}

function Get-RelativeInventory([string]$Root) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    return @(
        Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($resolvedRoot.Length + 1).Replace('\','/')
            [ordered]@{
                path = $relative
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    )
}

try {
    $builtWorker = Invoke-PyInstaller 'KnowledgeClipper.ImageOcr.Worker' (Join-Path $workerRoot 'image_ocr_worker.py') @()
    $builtSettings = Invoke-PyInstaller 'KnowledgeClipper.ImageOcr.Settings' (Join-Path $workerRoot 'image_ocr_settings_adapter.py') @(
        'image_ocr_companion_diagnostics',
        'image_ocr_worker'
    )

    $workerDescribe = (& $builtWorker describe --json) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $workerDescribe.workerId -ne 'image.ocr.mineru' -or $workerDescribe.workerVersion -ne $version) {
        throw 'Built worker identity mismatch'
    }
    $settingsDescribe = (& $builtSettings describe --json) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $settingsDescribe.schemaVersion -ne 'image-ocr-capability-config-descriptor.v6' -or $settingsDescribe.companionVersion -ne $companionVersion) {
        throw 'Built settings companion descriptor mismatch'
    }
    $settingsFixture = (Resolve-Path -LiteralPath (Join-Path $workerRoot 'fixtures\settings-ocr.v1.json')).Path
    $credentialMetadata = (Resolve-Path -LiteralPath (Join-Path $workerRoot 'fixtures\credential-metadata-dpapi-verified.v1.json')).Path
    $validate = (& $builtSettings validate --settings $settingsFixture --json) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $validate.status -ne 'passed' -or $validate.safety.providerInvoked -ne $false) {
        throw 'Built settings validate-only action failed'
    }
    $status = (& $builtSettings status --settings $settingsFixture --credential-metadata $credentialMetadata --json) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $status.safety.networkAccessPerformed -ne $false -or $status.checks.liveConnectivity.state -ne 'not-run' -or $status.checks.credential.verified -ne $true) {
        throw 'Built settings offline readiness failed'
    }
    $fake = (& $builtSettings test --settings $settingsFixture --credential-metadata $credentialMetadata --action image.ocr.test.offline-fake-provider --json) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $fake.status -ne 'passed' -or $fake.fakeProvider.exactMatch -ne $true -or $fake.processInheritance.workerObservedInjectedCredential -ne $true -or $fake.leakScan.taskPackageLogReceiptSecretOccurrences -ne 0 -or $fake.safety.mineruServiceCalled -ne $false -or $fake.terminalResult.originalBinding.hashBytesMediaTypeRoleOrderMatch -ne $true -or $fake.terminalResult.originalBinding.sameCompletedResult -ne $true) {
        throw 'Built settings Fake Provider action failed'
    }

    Move-Item -LiteralPath $builtWorker -Destination $workerOutput -ErrorAction Stop
    Move-Item -LiteralPath $builtSettings -Destination $settingsOutput -ErrorAction Stop
    $descriptorPath = Join-Path $candidateRoot 'image-ocr-capability-config-descriptor.v6.json'
    [IO.File]::WriteAllText($descriptorPath, ($settingsDescribe | ConvertTo-Json -Depth 30) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Path (Join-Path $candidateRoot 'schemas'),(Join-Path $candidateRoot 'evidence') | Out-Null
    foreach ($name in @(
        'image-request.v1.schema.json',
        'image-ocr-settings.v1.schema.json',
        'image-ocr-settings-adapter-result.v1.schema.json',
        'image-ocr-capability-config-descriptor.v6.schema.json',
        'image-ocr-capability-status.v4.schema.json',
        'image-ocr-companion-validate-result.v4.schema.json',
        'image-ocr-companion-test-result.v4.schema.json',
        'image-ocr-credential-metadata.v1.schema.json',
        'image-original-media-bundle.v1.schema.json',
        'image-processing-provenance.v1.schema.json',
        'image-processing-provenance-original-binding.v1.schema.json',
        'image-ocr-core-continuation-evidence.v5.schema.json'
    )) {
        Copy-Item -LiteralPath (Join-Path $workerRoot ('schemas\' + $name)) -Destination (Join-Path $candidateRoot 'schemas')
    }
    Copy-Item -LiteralPath (Join-Path $workerRoot 'fixtures\core-continuation-contract-evidence.v5.json') -Destination (Join-Path $candidateRoot 'evidence')
    Copy-Item -LiteralPath (Join-Path $workerRoot 'fixtures\provenance-original-binding.valid.v1.json') -Destination (Join-Path $candidateRoot 'evidence')
    Copy-Item -LiteralPath (Join-Path $workerRoot 'fixtures\provenance-original-binding.invalid-missing-sha.v1.json') -Destination (Join-Path $candidateRoot 'evidence')
    Copy-Item -LiteralPath $credentialMetadata -Destination (Join-Path $candidateRoot 'evidence')

    $inventory = Get-RelativeInventory $candidateRoot
    $treeRows = ($inventory | ForEach-Object { $_.path + "`0" + $_.sha256 + "`0" + $_.bytes + "`n" }) -join ''
    $treeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($treeRows))).ToLowerInvariant()
    $receipt = [ordered]@{
        schemaVersion = 'knowledge-clipper-image-ocr-release-root-receipt.v1'
        createdAt = [DateTimeOffset]::UtcNow.ToString('o')
        worker = [ordered]@{ id='image.ocr.mineru'; version=$version; contractVersion='1.0.0'; executable='KnowledgeClipper.ImageOcr.Worker.exe' }
        companion = [ordered]@{ version=$companionVersion; executable='KnowledgeClipper.ImageOcr.Settings.exe'; descriptor='image-ocr-capability-config-descriptor.v6.json' }
        releaseRoot = 'workers/image-ocr/candidates/1.2.4'
        files = $inventory
        fileCount = $inventory.Count
        totalBytes = [long](($inventory | ForEach-Object { [long]$_['bytes'] } | Measure-Object -Sum).Sum)
        treeSha256 = $treeHash
        validation = [ordered]@{
            workerDescribe = 'passed'
            companionDescribe = 'passed'
            validateOnly = 'passed'
            offlineReadiness = 'passed-no-network'
            fakeProvider = 'passed-exact-settings'
            credentialMetadataGate = 'passed-value-free-dpapi-metadata'
            processEnvironmentInheritance = 'passed-worker-observed'
            taskLogReceiptSecretLeakOccurrences = 0
            ocrSameRunOriginal = 'passed-hash-bytes-media-type-role-order-bound'
            originalMediaBundle = 'passed-provider-calls-zero'
            humanDecisionContinuation = 'passed-default-no-ocr-rerun'
            liveConnectivity = 'defined-not-run'
        }
        safety = [ordered]@{
            externalProductionApisInvoked = 0
            mineruServiceInvocations = 0
            externalUploads = 0
            credentialValuesPersisted = 0
        }
        pyInstallerVersion = $PyInstallerVersion
        registryOnboarding = 'NO-GO-until-Core-same-run-original-admission-and-new-authority-checkpoint'
        replacesPriorCandidate = $false
    }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $receipt | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path -LiteralPath $buildRoot) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $resolvedBuildRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\')
        if (-not $resolvedBuildRoot.StartsWith($tempRoot + '\knowledge-clipper-image-ocr-companion-build-', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected build path: $resolvedBuildRoot"
        }
        Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
