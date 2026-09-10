param(
    [Parameter(Mandatory = $true)][string]$CandidateRoot,
    [string]$EvidenceRoot = (Join-Path 'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki-action' ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))),
    [string[]]$Modes = @('meaning-reading', 'writing', 'both')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $CandidateRoot 'KnowledgeClipper.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Candidate executable is missing: $exe" }

[IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$anki = [ordered]@{
    schema_version = 'anki-export-config.v1'
    endpoint = 'http://127.0.0.1:8765'
    expected_profile = '账户 1'
    timeout_seconds = 15
    max_media_bytes = 26214400
    provision_managed = $false
    deck_routes = [ordered]@{ canary = '零碎知识剪藏::Canary::PhaseB-20260828T081109Z' }
    note_types = [ordered]@{
        basic_qa = [ordered]@{ model_name='RQC Basic v1'; field_map=[ordered]@{question='Front';answer='Back';source='Source'}; required_fields=@('question','answer') }
        language_item = [ordered]@{ model_name='RQC Basic v1'; field_map=[ordered]@{context_sentence='Front';reading_and_meaning='Back'}; required_fields=@('context_sentence','reading_and_meaning') }
        language_writing = [ordered]@{ model_name='RQC Basic v1'; field_map=[ordered]@{front='Front';back='Back'}; required_fields=@('front','back') }
    }
} | ConvertTo-Json -Depth 20 -Compress

$cases = @(
    [ordered]@{ Mode='meaning-reading'; Text='The archivist followed a deliberate process before accepting the recovered record. The target word deliberate /dɪˈlɪbərət/ means 深思熟虑的。' },
    [ordered]@{ Mode='writing'; Text='The maintainer prepared an explicit recovery guide for the portable package; /ɪkˈsplɪsɪt/ means 明确清楚的。' },
    [ordered]@{ Mode='both'; Text='The reviewer required a comprehensive verification before admitting the candidate; /ˌkɑːmprɪˈhensɪv/ means 全面详尽的。' }
)
$cases = @($cases | Where-Object { $Modes -contains $_.Mode })
if ($cases.Count -eq 0) { throw 'No supported matrix mode was selected.' }

$savedPath = $env:PATH
$savedEdge = $env:KNOWLEDGE_CLIPPER_EDGE_TTS
$savedFfplay = $env:KNOWLEDGE_CLIPPER_FFPLAY
try {
    $env:PATH = 'C:\Windows\System32'
    Remove-Item Env:KNOWLEDGE_CLIPPER_EDGE_TTS -ErrorAction SilentlyContinue
    Remove-Item Env:KNOWLEDGE_CLIPPER_FFPLAY -ErrorAction SilentlyContinue

    foreach ($case in $cases) {
        $mode = $case.Mode
        $jobsRoot = Join-Path $EvidenceRoot "jobs-$mode"
        $inputPath = Join-Path $EvidenceRoot "input-$mode.json"
        $receiptPath = Join-Path $EvidenceRoot "receipt-$mode.json"
        $stdoutPath = Join-Path $EvidenceRoot "stdout-$mode.log"
        $stderrPath = Join-Path $EvidenceRoot "stderr-$mode.log"
        $input = [ordered]@{
            Text = $case.Text
            LanguageChoice = $mode
            AnkiConfigurationJson = $anki
            JobsRoot = $jobsRoot
            TimeoutSeconds = 300
        }
        [IO.File]::WriteAllText($inputPath, ($input | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
        $process = Start-Process -FilePath $exe -ArgumentList @('--candidate-read-to-anki-canary', $inputPath, $receiptPath) -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "$mode candidate canary failed with exit code $($process.ExitCode); stderr=$stderrPath" }
        if (-not (Test-Path -LiteralPath $receiptPath)) { throw "$mode receipt is missing" }
    }
}
finally {
    $env:PATH = $savedPath
    if ($null -eq $savedEdge) { Remove-Item Env:KNOWLEDGE_CLIPPER_EDGE_TTS -ErrorAction SilentlyContinue } else { $env:KNOWLEDGE_CLIPPER_EDGE_TTS = $savedEdge }
    if ($null -eq $savedFfplay) { Remove-Item Env:KNOWLEDGE_CLIPPER_FFPLAY -ErrorAction SilentlyContinue } else { $env:KNOWLEDGE_CLIPPER_FFPLAY = $savedFfplay }
}

$EvidenceRoot
