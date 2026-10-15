[CmdletBinding()]
param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = 'D:\Applications\RemNoteQuickClipperRuntime\codex-python'
if (-not (Test-Path -LiteralPath "$root\Scripts\python.exe")) {
    & $Python -m venv $root
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& "$root\Scripts\python.exe" -m pip install --cache-dir 'D:\Caches\pip' 'openai-codex==0.159.0'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Keep the validated official binary independent of desktop-app auto-updates.
& npm install --prefix 'D:\Applications\RemNoteQuickClipperRuntime\codex-runtime' --cache 'D:\Caches\npm-cache' --no-audit --no-fund --save-exact '@openai/codex@0.160.1'
exit $LASTEXITCODE
