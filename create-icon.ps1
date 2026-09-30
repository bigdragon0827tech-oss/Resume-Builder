Add-Type -AssemblyName System.Drawing

$source = Join-Path $PWD "Assets\ResumeBuilder.png"
$output = Join-Path $PWD "Assets\ResumeBuilder.ico"

$sizes = @(16, 24, 32, 48, 64, 128, 256)

$sourceImage = [System.Drawing.Image]::FromFile($source)

try {
    $images = @()

    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size

        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode =
                    [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode =
                    [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode =
                    [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

                $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
            }
            finally {
                $graphics.Dispose()
            }

            $stream = New-Object System.IO.MemoryStream
            $bitmap.Save(
                $stream,
                [System.Drawing.Imaging.ImageFormat]::Png)

            $images += ,$stream.ToArray()
            $stream.Dispose()
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $file = [System.IO.File]::Create($output)

    try {
        $writer = New-Object System.IO.BinaryWriter $file

        try {
            # ICONDIR
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$images.Count)

            $offset = 6 + (16 * $images.Count)

            for ($i = 0; $i -lt $images.Count; $i++) {
                $size = $sizes[$i]
                $data = $images[$i]

                # 0 represents 256 in ICO format.
                if ($size -eq 256) {
                    $writer.Write([Byte]0)
                    $writer.Write([Byte]0)
                }
                else {
                    $writer.Write([Byte]$size)
                    $writer.Write([Byte]$size)
                }

                $writer.Write([Byte]0)
                $writer.Write([Byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$data.Length)
                $writer.Write([UInt32]$offset)

                $offset += $data.Length
            }

            foreach ($data in $images) {
                $writer.Write($data)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $file.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

Write-Host "Created:"
Write-Host $output