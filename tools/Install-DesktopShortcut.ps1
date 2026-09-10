[CmdletBinding()]
param(
    [string]$ExecutablePath = 'D:\Artifacts\KnowledgeClipper\build\RemNoteQuickClipper.Desktop\Release\net8.0-windows\win-x64\KnowledgeClipper.exe'
)
$ErrorActionPreference = 'Stop'
$executable = (Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop).FullName
$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopDirectory 'KnowledgeClipper.lnk'
$legacyShortcut = Join-Path $desktopDirectory 'RemNote 剪藏.lnk'
# Reuse the former desktop entry instead of leaving an obsolete launcher beside it.
if ((Test-Path -LiteralPath $legacyShortcut) -and -not (Test-Path -LiteralPath $shortcutPath)) {
    Move-Item -LiteralPath $legacyShortcut -Destination $shortcutPath
}
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = Split-Path -Parent $executable
    $shortcut.IconLocation = $executable + ',0'
    $shortcut.Description = '零碎知识剪藏 · 当前正式版本'
    $shortcut.WindowStyle = 1
    $shortcut.Save()
    $readback = $shell.CreateShortcut($shortcutPath)
    if ($readback.TargetPath -ne $executable -or $readback.WorkingDirectory -ne (Split-Path -Parent $executable) -or $readback.Arguments -ne '') {
        throw '桌面快捷方式回读与正式入口不符。'
    }
    Write-Output $shortcutPath
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}
