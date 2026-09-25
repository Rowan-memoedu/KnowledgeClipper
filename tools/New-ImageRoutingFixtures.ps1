param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$items = [ordered]@{
    'question-1.png' = 'A rectangle has width 7 cm.'
    'question-2.png' = "Its length is 9 cm.`nFind its perimeter and area."
    'answer-1.png' = 'Perimeter = 2 x (7 + 9) = 32 cm.'
    'answer-2.png' = 'Area = 7 x 9 = 63 square cm.'
    'ocr.png' = "Learning connects ideas.`nPractice helps us remember."
}
foreach ($item in $items.GetEnumerator()) {
    $bitmap = [Drawing.Bitmap]::new(1000, 220)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $font = [Drawing.Font]::new('Segoe UI', 28)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.DrawString($item.Value, $font, [Drawing.Brushes]::Black, [Drawing.PointF]::new(25, 30))
        $bitmap.Save((Join-Path $output $item.Key), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $font.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
Write-Output $output
