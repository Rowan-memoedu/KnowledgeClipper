$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $project 'dist\RemNoteQuickClipper.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Executable not found: $exe" }
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcut = Join-Path $desktop 'RemNote 剪藏.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = Split-Path -Parent $exe
$link.Description = '选择文本或使用剪贴板，经 DeepSeek / MinerU 审查后写入 RemNote'
$link.Save()
Get-Item -LiteralPath $shortcut | Select-Object FullName,Length,LastWriteTime
