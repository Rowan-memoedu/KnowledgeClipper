param(
    [string]$RepositoryRoot = 'D:\Projects\RemNote Quick Clipper',
    [string]$ArtifactsRoot = 'D:\Artifacts\KnowledgeClipper\read-to-anki-runner',
    [string]$DotnetPath = 'D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe',
    [string]$ChildIdentityEvidenceRoot,
    [string]$ChildIdentityProbeDll,
    [string]$PreviewRaceEvidenceRoot,
    [string]$ResumeStagingRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant() }
function Write-Utf8Json([string]$Path,[object]$Value) { [IO.File]::WriteAllText($Path,(ConvertTo-Json $Value -Depth 20)+"`n",[Text.UTF8Encoding]::new($false)) }

$repo=[IO.Path]::GetFullPath($RepositoryRoot)
$project=Join-Path $repo 'tools\actual-harness\KnowledgeClipper.ActualDesktopFlowRunner\KnowledgeClipper.ActualDesktopFlowRunner.csproj'
$program=Join-Path $repo 'tools\actual-harness\KnowledgeClipper.ActualDesktopFlowRunner\Program.cs'
$sourceHost=Join-Path $repo 'tools\actual-harness\KnowledgeClipper.ActualDesktopFlowRunner\ExternalTextSource.cs'
$wrapper=Join-Path $repo 'tools\actual-harness\Invoke-ActualDesktopFlowRunnerV4.ps1'
$timestamp=[datetime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$releaseId='actual-desktop-flow-runner-v5-'+$timestamp
$artifacts=[IO.Path]::GetFullPath($ArtifactsRoot)
$staging=Join-Path $artifacts ('.staging-'+$releaseId+'-'+[guid]::NewGuid().ToString('N'))
if($ResumeStagingRoot){
    $staging=(Resolve-Path -LiteralPath $ResumeStagingRoot -ErrorAction Stop).Path
    $stageName=Split-Path $staging -Leaf
    if((Split-Path $staging -Parent)-ne$artifacts-or$stageName-notmatch'^\.staging-(actual-desktop-flow-runner-v5-\d{8}T\d{6}Z)-[a-f0-9]+$'){throw 'runner-resume-staging-outside-artifacts'}
    $releaseId=$Matches[1]
    if(-not$PreviewRaceEvidenceRoot-or(Test-Path (Join-Path $staging 'runner-release-manifest.v5.json'))){throw 'runner-resume-requires-unsealed-preview-staging'}
}
$release=Join-Path $artifacts $releaseId
if(Test-Path -LiteralPath $release){throw 'runner-release-already-exists'}
[IO.Directory]::CreateDirectory((Join-Path $staging 'app'))|Out-Null
try {
    if(-not$ResumeStagingRoot){
        & $DotnetPath publish $project -c Release -r win-x64 --self-contained true --no-restore --nologo -o (Join-Path $staging 'app') /p:PublishSingleFile=false
        if($LASTEXITCODE-ne0){throw 'runner-publish-failed:'+ $LASTEXITCODE}
        Copy-Item -LiteralPath $wrapper -Destination (Join-Path $staging 'Invoke-ActualDesktopFlowRunnerV4.ps1')
        Copy-Item -LiteralPath (Join-Path (Split-Path $program) 'SEQUENCE.md') -Destination (Join-Path $staging 'SEQUENCE.md')
    } else {
        Write-Output 'PHASE resume exact published binary; no rebuild'
        if((Get-Sha256 $wrapper)-ne(Get-Sha256 (Join-Path $staging 'Invoke-ActualDesktopFlowRunnerV4.ps1'))-or(Get-Sha256 (Join-Path (Split-Path $program) 'SEQUENCE.md'))-ne(Get-Sha256 (Join-Path $staging 'SEQUENCE.md'))){throw 'runner-resume-source-copy-drift'}
    }
    foreach($required in @('app\KnowledgeClipper.ActualDesktopFlowRunner.exe','app\KnowledgeClipper.ActualDesktopFlowRunner.dll','app\KnowledgeClipper.ActualDesktopFlowRunner.runtimeconfig.json','app\KnowledgeClipper.ActualDesktopFlowRunner.deps.json','app\hostpolicy.dll')){if(-not(Test-Path -LiteralPath (Join-Path $staging $required))){throw 'runner-publish-required-file-missing:'+ $required}}
    $evidence=Join-Path $staging 'evidence'
    [IO.Directory]::CreateDirectory($evidence)|Out-Null
    $entry=Join-Path $staging 'app\KnowledgeClipper.ActualDesktopFlowRunner.exe'
    $tests=if($PreviewRaceEvidenceRoot){@('preview-barrier-self-test')}else{@('source-loop-self-test','self-test')}
    foreach($test in $tests) {
        Write-Output ('PHASE runner offline '+$test)
        $testLog=if($ResumeStagingRoot){$test+'.resume-'+$timestamp}else{$test}
        $stdout=Join-Path $evidence ($testLog+'.stdout.jsonl');$stderr=Join-Path $evidence ($testLog+'.stderr.log')
        if((Test-Path $stdout)-or(Test-Path $stderr)){throw 'runner-test-evidence-already-exists'}
        & $entry ('--'+$test) 1> $stdout 2> $stderr
        if($LASTEXITCODE-ne0){$testExitCode=$LASTEXITCODE;Get-Content -LiteralPath $stderr|Write-Output;throw ('runner-offline-test-failed:'+ $test + ':' + $testExitCode)}
        $lines=@(Get-Content -LiteralPath $stdout | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $result=$lines[-1]|ConvertFrom-Json -ErrorAction Stop
        if($result.status-ne'PASS'){throw ('runner-offline-result-not-pass:'+ $test)}
        if($test-eq'preview-barrier-self-test') {
            if($result.previewBarrierCases-ne9-or$result.externalCalls-ne0-or$result.businessTasks-ne0){throw 'runner-preview-barrier-gate-failed'}
        } elseif($test-eq'source-loop-self-test') {
            if($result.legacyUnpumpedSourceRejected-ne$true-or$result.independentStaSourceCases-ne2-or$result.realCrossProcessTextPattern-ne$true-or$result.externalCalls-ne0-or$result.businessSubmissions-ne0){throw 'runner-source-loop-gate-failed'}
        } else {
            $sequenceResult=@($lines|ForEach-Object{$_|ConvertFrom-Json}|Where-Object{$_.PSObject.Properties.Name -contains 'sequenceCases'})
            if($sequenceResult.Count-ne1-or$sequenceResult[0].sequenceCases-ne9-or$sequenceResult[0].singleProcess-ne$true-or$sequenceResult[0].externalCalls-ne0){throw 'runner-sequence-regression-gate-failed'}
            $settingsResult=@($lines|ForEach-Object{$_|ConvertFrom-Json}|Where-Object{$_.PSObject.Properties.Name -contains 'settingsDestinationCases'})
            if($settingsResult.Count-ne1-or$settingsResult[0].settingsDestinationCases-ne9-or$settingsResult[0].settingsBeforeContentAndTts-ne$true-or$settingsResult[0].externalCalls-ne0){throw 'runner-settings-destination-regression-gate-failed'}
            if($result.captureFixtureCases-ne10){throw 'runner-capture-fixture-regression-gate-failed'}
            if($result.childIdentityFixtures-ne6){throw 'runner-child-identity-regression-gate-failed'}
            if($result.uiTerminalCases-ne11){throw 'runner-ui-terminal-regression-gate-failed'}
            if($result.semanticCases-ne6-or$result.semanticAcceptedCases-ne3-or$result.acceptedCases-ne1-or$result.staleCommandRejected-ne$true-or$result.contentMismatchRejected-ne$true-or$result.negativeAnkiWrites-ne0-or$result.wrongDestinationRejectedBeforeConfirm-ne$true-or$result.delayedPlaybackCompletionAfterCards-ne$true-or$result.externalConnections-ne0-or$result.businessTasks-ne0){throw 'runner-semantic-regression-gate-failed'}
        }
        Write-Output $lines[-1]
    }
    $previewRaceBinding=$null
    if($PreviewRaceEvidenceRoot){
        $racePath=Join-Path $PreviewRaceEvidenceRoot 'race-receipt.json';$race=Get-Content -LiteralPath $racePath -Raw|ConvertFrom-Json
        $exitPath=Join-Path $PreviewRaceEvidenceRoot 'host/host-exit.json';$hostExit=Get-Content -LiteralPath $exitPath -Raw|ConvertFrom-Json
        if($race.status-ne'PASS'-or$race.scenarios.Count-ne5-or@($race.scenarios|Where-Object status -ne 'PASS').Count-ne0-or$race.externalCalls-ne0-or$race.confirmInvocations-ne0-or$hostExit.businessInvocations-ne0){throw 'runner-real-form-preview-race-gate-failed'}
        if($race.runner.sha256-ne(Get-Sha256 (Join-Path $staging 'app/KnowledgeClipper.ActualDesktopFlowRunner.dll'))){throw 'runner-real-form-preview-probe-binary-drift'}
        Copy-Item -LiteralPath $racePath -Destination (Join-Path $evidence 'real-form-preview-race.json')
        Copy-Item -LiteralPath $exitPath -Destination (Join-Path $evidence 'real-form-host-exit.json')
        $previewRaceBinding=[ordered]@{sourceRoot=$PreviewRaceEvidenceRoot;receiptSha256=Get-Sha256 $racePath;hostExitSha256=Get-Sha256 $exitPath;scenarios=5;actualRun=$false;fullBusinessMatrixReexecuted=$false;sourceLoopReexecuted=$false}
    }
    if($ChildIdentityEvidenceRoot) {
        if(-not $ChildIdentityProbeDll-or(Get-Sha256 $ChildIdentityProbeDll)-ne(Get-Sha256 (Join-Path $staging 'app\KnowledgeClipper.ActualDesktopFlowRunner.dll'))){throw 'offline-modal-probe-binary-drift'}
        $probe=Get-Content -Raw -LiteralPath (Join-Path $ChildIdentityEvidenceRoot 'identity-probe.json')|ConvertFrom-Json
        if($probe.status-ne'PASS'-or$probe.rawObservations-lt2-or$probe.uniqueIdentities-ne1-or$probe.invocations-ne0-or$probe.externalCalls-ne0-or$probe.businessSubmissions-ne0){throw 'offline-modal-probe-not-qualified'}
        $identities=@($probe.observations.identity|Sort-Object -Unique)
        $roots=@($probe.observations.rootHandle|Sort-Object -Unique)
        if($identities.Count-ne1-or$roots.Count-lt2){throw 'offline-modal-duplicate-topology-not-reproduced'}
        Copy-Item -LiteralPath (Join-Path $ChildIdentityEvidenceRoot 'identity-probe.json') -Destination (Join-Path $evidence 'owned-modal-identity-probe.json')
        Copy-Item -LiteralPath (Join-Path $ChildIdentityEvidenceRoot 'host\host-ready.json') -Destination (Join-Path $evidence 'owned-modal-host-ready.json')
        Copy-Item -LiteralPath (Join-Path $ChildIdentityEvidenceRoot 'host\host-exit.json') -Destination (Join-Path $evidence 'owned-modal-host-exit.json')
    }
    $files=@(Get-ChildItem -LiteralPath $staging -File -Recurse|ForEach-Object{$relative=[IO.Path]::GetRelativePath($staging,$_.FullName).Replace('\','/');[ordered]@{path=$relative;bytes=$_.Length;sha256=Get-Sha256 $_.FullName}}|Sort-Object path)
    [long]$bytes=0;foreach($file in $files){$bytes+=[long]$file.bytes}
    $manifest=[ordered]@{
        schemaVersion='knowledge-clipper-actual-runner-release.v5'
        releaseId=$releaseId
        createdAtUtc=[datetimeoffset]::UtcNow.ToString('O')
        qualificationScope=if($PreviewRaceEvidenceRoot){'preview-render-identity-race-only'}else{'runner-offline'}
        previewRaceBinding=$previewRaceBinding
        resumedPublishedStaging=[bool]$ResumeStagingRoot
        runtime=[ordered]@{model='self-contained';rid='win-x64';targetFramework='net8.0-windows';singleFile=$false}
        entrypoint='app/KnowledgeClipper.ActualDesktopFlowRunner.exe'
        requiredRuntimeFiles=@('app/KnowledgeClipper.ActualDesktopFlowRunner.runtimeconfig.json','app/KnowledgeClipper.ActualDesktopFlowRunner.deps.json','app/hostpolicy.dll')
        sourceBindings=[ordered]@{
            releaseBuilder=[ordered]@{path=$PSCommandPath;sha256=Get-Sha256 $PSCommandPath}
            program=[ordered]@{path=$program;bytes=(Get-Item $program).Length;sha256=Get-Sha256 $program}
            ankiSettingsEvidence=[ordered]@{path=(Join-Path (Split-Path $program) 'AnkiSettingsEvidence.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'AnkiSettingsEvidence.cs')}
            sequencePlan=[ordered]@{path=(Join-Path (Split-Path $program) 'SequencePlan.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'SequencePlan.cs')}
            sequenceSelfTest=[ordered]@{path=(Join-Path (Split-Path $program) 'SequenceSelfTest.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'SequenceSelfTest.cs')}
            sequenceWorkerLedger=[ordered]@{path=(Join-Path (Split-Path $program) 'SequenceWorkerLedger.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'SequenceWorkerLedger.cs')}
            previewReadBarrier=[ordered]@{path=(Join-Path (Split-Path $program) 'PreviewReadBarrier.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'PreviewReadBarrier.cs')}
            uiaPreviewSource=[ordered]@{path=(Join-Path (Split-Path $program) 'UiaPreviewSource.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'UiaPreviewSource.cs')}
            previewRaceProbe=[ordered]@{path=(Join-Path (Split-Path $program) 'PreviewRaceProbe.cs');sha256=Get-Sha256 (Join-Path (Split-Path $program) 'PreviewRaceProbe.cs')}
            externalTextSource=[ordered]@{path=$sourceHost;bytes=(Get-Item $sourceHost).Length;sha256=Get-Sha256 $sourceHost}
            project=[ordered]@{path=$project;bytes=(Get-Item $project).Length;sha256=Get-Sha256 $project}
            wrapper=[ordered]@{path=$wrapper;bytes=(Get-Item $wrapper).Length;sha256=Get-Sha256 $wrapper}
        }
        payload=[ordered]@{files=$files.Count;bytes=$bytes}
        files=$files
    }
    Write-Utf8Json (Join-Path $staging 'runner-release-manifest.v5.json') $manifest
    Move-Item -LiteralPath $staging -Destination $release
    Write-Output $release
}
catch {
    Write-Warning ('Unpromoted staging retained for failure evidence: '+$staging)
    throw
}
