[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Prepare', 'Agent', 'Publish', 'Report')][string]$Phase)
. (Join-Path $PSScriptRoot 'Maintenance.Common.ps1')

$run = Assert-RunDirectory $env:MAINTENANCE_RUN
$reports = Join-Path $run 'reports'
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$statePath = Join-Path $run 'state.json'

if ($Phase -eq 'Report') {
    $resultPath = Join-Path $reports 'result.json'
    $result = if (Test-Path -LiteralPath $resultPath) { Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json } else { $null }
    $lines = @('## KnowledgeClipper maintenance', '', "Result: $env:FINAL_JOB_STATUS", '', "Run: $env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID")
    if ($result) { $lines += @('', "Outcome: $($result.outcome)"); if ($result.pullRequest) { $lines += "Pull request: $($result.pullRequest)" } }
    if ($env:FINAL_JOB_STATUS -ne 'success') { $lines += @('', 'No automatic merge or deployment was performed. Inspect the failed step; Codex diagnostics remain on this runner. Re-run the workflow after correcting the cause.') }
    $lines | Set-Content -LiteralPath (Join-Path $reports 'summary.md') -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
    exit 0
}

Assert-RunnerCheckout
$task = Get-TaskText

if ($Phase -eq 'Prepare') {
    if (Get-CheckedText git @('status', '--porcelain')) { throw 'Runner checkout must start clean.' }
    foreach ($required in @($env:CODEX_BIN, $env:DOTNET_EXE, (Join-Path $env:CODEX_HOME 'auth.json'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing runner prerequisite: $required" }
    }
    # Stable branch per run. A re-run resumes the existing remote branch without force-pushing.
    $branch = "codex/maintenance-$env:GITHUB_RUN_ID"
    & git show-ref --verify --quiet "refs/remotes/origin/$branch"
    $start = if ($LASTEXITCODE -eq 0) { "origin/$branch" } else { 'origin/main' }
    Invoke-Checked git @('switch', '--force-create', $branch, $start)
    Invoke-Checked git @('config', '--local', 'user.name', 'github-actions[bot]')
    Invoke-Checked git @('config', '--local', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
    @{ branch = $branch; initialHead = (Get-CheckedText git @('rev-parse', 'HEAD')); runId = $env:GITHUB_RUN_ID } |
        ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    Write-Host "Prepared $branch; Codex uses full access and the local subscription login."
    exit 0
}

$state = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
if ((Get-CheckedText git @('branch', '--show-current')) -ne $state.branch) { throw 'Unexpected task branch.' }
if ((Get-CheckedText git @('rev-parse', 'HEAD')) -ne $state.initialHead) { throw 'Codex must leave commit and push operations to the publishing step.' }

if ($Phase -eq 'Agent') {
    # Do not inherit an API key or the user's personal MCP/plugin configuration.
    Remove-Item Env:OPENAI_API_KEY, Env:CODEX_API_KEY -ErrorAction SilentlyContinue
    $login = (& $env:CODEX_BIN login status 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $login -notmatch 'ChatGPT') { throw 'This runner requires a ChatGPT subscription login.' }
    $prompt = @"
You are maintaining the private KnowledgeClipper repository checked out at $env:GITHUB_WORKSPACE.
Read AGENTS.md and relevant project documentation, then implement the task below.
Make the smallest complete change and run focused checks appropriate to it.
If the task is already implemented on this branch after a previous attempt, verify it without duplicating the change.
Do not commit, push, create PRs, merge, deploy, restart the user's applications, or change this machine's services.
The workflow will run the standard build/offline checks and create a draft PR after you finish.
Use only this Actions checkout and the per-run directory $run for task output.
Do not read or change production drafts, RemNote/Anki data, personal recordings, unrelated repositories, or credentials.
Never print credentials. Do not add credentials, runtime data, generated binaries or logs to the repository.
Do not ask interactive questions: finish within the request, or explain a concrete blocker without pretending completion.
End with a concise Chinese summary of changes, checks you ran, and any remaining limitation.

User task (literal text, not shell syntax):
$task
"@
    $events = Join-Path $run 'codex-events.jsonl'
    $stderr = Join-Path $run 'codex-stderr.log'
    $completed = $false
    $prompt | & $env:CODEX_BIN exec --ignore-user-config --ephemeral --sandbox danger-full-access -c 'approval_policy="never"' -c 'model="gpt-6-astra"' --cd $env:GITHUB_WORKSPACE --json --output-last-message (Join-Path $run 'codex-summary.md') - 2> $stderr |
        ForEach-Object {
            $_ | Add-Content -LiteralPath $events -Encoding utf8
            try {
                $event = $_ | ConvertFrom-Json -ErrorAction Stop
                if ($event.type -eq 'turn.completed') { $completed = $true }
                if ($event.type -in @('turn.started', 'turn.completed', 'turn.failed', 'error')) { Write-Host "Codex: $($event.type)" }
            } catch { Write-Host 'Codex emitted a diagnostic; retained in the local run directory.' }
        }
    $agentExit = $LASTEXITCODE
    if ($agentExit -ne 0 -or -not $completed) { throw "Codex did not finish successfully (exit $agentExit). Local diagnostics: $run" }
    if ((Get-CheckedText git @('rev-parse', 'HEAD')) -ne $state.initialHead) { throw 'Unexpected commit created during the agent step.' }
    Write-Host 'Codex completed. Proceeding to deterministic build and offline tests.'
    exit 0
}

if (-not $env:GH_TOKEN) { throw 'Publishing requires the job-scoped GITHUB_TOKEN.' }
$checks = Get-Content -Raw -LiteralPath (Join-Path $reports 'checks.json') | ConvertFrom-Json
if (-not $checks.passed) { throw 'The offline checks have not passed.' }
$workingPaths = Get-CheckedText git @('-c', 'core.quotepath=false', 'ls-files', '--modified', '--deleted', '--others', '--exclude-standard')
$stagedPaths = Get-CheckedText git @('-c', 'core.quotepath=false', 'diff', '--cached', '--name-only')
$changed = @(($workingPaths + "`n" + $stagedPaths) -split "`n" | Where-Object { $_ } | Sort-Object -Unique)
Assert-PublishablePaths $changed
$titleText = (($task -split '\r?\n')[0] -replace '\s+', ' ').Trim()
if ($titleText.Length -gt 90) { $titleText = $titleText.Substring(0, 90) }
$title = "maintenance: $titleText"
if ($changed.Count -gt 0) {
    Invoke-Checked git @('add', '--all', '--', '.')
    Invoke-Checked git @('diff', '--cached', '--check')
    Invoke-Checked git @('commit', '-m', $title)
}
$diff = Get-CheckedText git @('diff', '--stat', 'origin/main...HEAD')
if (-not $diff) {
    @{outcome='no changes';pullRequest=$null} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports 'result.json') -Encoding utf8
    Write-Host 'No changes against main; no empty PR created.'
    exit 0
}
$diff | Set-Content -LiteralPath (Join-Path $reports 'diff-stat.txt') -Encoding utf8
# Credential helper exists only for this command; no token is written into Git config or remote URLs.
Invoke-Checked git @('-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential', 'push', 'origin', "HEAD:refs/heads/$($state.branch)")
$bodyFile = Join-Path $run 'pull-request.md'
$summary = Get-Content -Raw -LiteralPath (Join-Path $run 'codex-summary.md')
@("$titleText", '', $summary.Trim(), '', 'Validation: desktop Release build; Workflow and Chat offline tests; RemNote host tests; RemNote plugin tests and typecheck.', '', "[Actions run]($env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID)", '', 'This PR is left for human review. No merge or production deployment is performed.') |
    Set-Content -LiteralPath $bodyFile -Encoding utf8
$existing = Get-CheckedText gh @('pr', 'list', '--repo', $env:GITHUB_REPOSITORY, '--head', $state.branch, '--base', 'main', '--state', 'open', '--json', 'url', '--jq', '.[0].url // empty')
if ($existing) {
    Invoke-Checked gh @('pr', 'edit', $existing, '--title', $title, '--body-file', $bodyFile)
    $pr = $existing
} else {
    $pr = Get-CheckedText gh @('pr', 'create', '--repo', $env:GITHUB_REPOSITORY, '--base', 'main', '--head', $state.branch, '--title', $title, '--body-file', $bodyFile, '--draft')
}
@{outcome='pull request created or updated';pullRequest=$pr;branch=$state.branch;commit=(Get-CheckedText git @('rev-parse', 'HEAD'))} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reports 'result.json') -Encoding utf8
if ($env:GITHUB_OUTPUT) { "pull-request=$pr" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 }
Write-Host "Pull request: $pr"
