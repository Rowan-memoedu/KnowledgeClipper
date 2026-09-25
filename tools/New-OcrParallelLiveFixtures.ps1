param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith('D:\Caches\KnowledgeClipper\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Expected an isolated D-drive cache directory.' }
$manifest = Join-Path $output 'public-fixtures.json'
if (Test-Path -LiteralPath $manifest) {
    foreach ($item in (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)) {
        if ((Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256) { throw 'Frozen public fixture changed.' }
    }
    Write-Output $manifest
    return
}
[IO.Directory]::CreateDirectory($output) | Out-Null
Add-Type -AssemblyName System.Drawing
$texts = @('Page 1. Learning connects ideas.', 'Page 2. Practice helps us remember.', 'Page 3. Clear examples support understanding.')
$items = @()
for ($index = 0; $index -lt $texts.Count; $index++) {
    $path = Join-Path $output ('public-{0}.png' -f ($index + 1))
    if (Test-Path -LiteralPath $path) { throw 'Unfrozen fixture exists; inspect before retrying preparation.' }
    $bitmap = [Drawing.Bitmap]::new(1080, 140)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $font = [Drawing.Font]::new('Segoe UI', 24)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.DrawString($texts[$index], $font, [Drawing.Brushes]::Black, [Drawing.PointF]::new(20, 42))
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    $items += [pscustomobject]@{ path = $path; text = $texts[$index]; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$items | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
Write-Output $manifest
