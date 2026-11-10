Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Checked {
    param([string]$FilePath, [string[]]$ArgumentList = @())
    & $FilePath @ArgumentList | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE" }
}

function Get-CheckedText {
    param([string]$FilePath, [string[]]$ArgumentList = @())
    $value = & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE" }
    return ($value -join "`n").Trim()
}

function Assert-RunDirectory {
    param([string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith('D:\Artifacts\GitHubActions\KnowledgeClipper\runs\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Run output must be inside the dedicated D: Actions artifacts directory.'
    }
    return $absolute
}

function Assert-RunnerCheckout {
    if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This command changes branches and must run in GitHub Actions.' }
    $root = [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)
    if (-not $root.StartsWith('D:\Artifacts\GitHubActions\KnowledgeClipper\work\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to modify a directory outside the dedicated runner checkout.'
    }
    if ($env:GITHUB_REPOSITORY -notin @('Rowan-memoedu/KnowledgeClipper', 'memoedu/KnowledgeClipper') -or $env:GITHUB_REF -ne 'refs/heads/main') {
        throw 'This workflow is restricted to KnowledgeClipper main.'
    }
    Set-Location -LiteralPath $root
}

function Get-TaskText {
    $task = $env:MAINTENANCE_TASK
    if ([string]::IsNullOrWhiteSpace($task)) { throw 'The task must not be empty.' }
    if ($task.Length -gt 24000) { throw 'The task is too long; provide a concise request and repository file references.' }
    return $task
}

function Assert-PublishablePaths {
    param([string[]]$Paths)
    foreach ($path in $Paths) {
        if ($path -match '(^|/)(auth\.json|credentials\.json|\.env($|\.(?!example$))|id_rsa|id_ed25519)(/|$)' -or
            $path -match '(?i)\.(pfx|p12|pem|key)$') {
            throw "Refusing to publish a credential-like path: $path"
        }
    }
}
