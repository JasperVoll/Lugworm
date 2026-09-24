[CmdletBinding()]
param(
    [switch]$VerifyOnly,
    [switch]$SkipRasterize
)

$ErrorActionPreference = 'Stop'

$scriptRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $scriptRoot '..\..\..')).Path
$sourceRoot = Join-Path $scriptRoot ''
$resourcesRoot = Join-Path $projectRoot 'Resources'
$edgePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'

$names = @(
    'IamSlicer', 'IamMover', 'IamGcode', 'IamGcodeSave', 'IamGcodeRestart',
    'IamRobot', 'IamRobotDefinition', 'IamPrintBed', 'IamOverhang', 'IamDoeDesign', 'IamRsm', 'IamDesirability',
    'IamTestGeo', 'IamStats', 'IamDuetConnect',
    'IamDuetStatus', 'IamDuetLive', 'IamDuetJog', 'IamDuetTerminal',
    'IamDuetPrint', 'IamDuetFiles', 'IamParetoSelect', 'IamLegend',
    'IamInterlayer', 'IamSettings'
)

function Assert-Manifest {
    $duplicates = @($names | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "Icon manifest contains duplicate names: $($duplicates.Name -join ', ')"
    }
    if ($names.Count -ne 25) {
        throw "Icon manifest must contain exactly 25 names; found $($names.Count)."
    }
}

function Get-IconPaths {
    param([string]$Name)
    [pscustomobject]@{
        Name = $Name
        Source = Join-Path $sourceRoot "$Name.svg"
        Output = Join-Path $resourcesRoot "$Name`_24.png"
    }
}

function Assert-Sources {
    $missing = @($names | ForEach-Object { $path = Join-Path $sourceRoot "$_.svg"; if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $path } })
    if ($missing.Count -gt 0) {
        throw "Missing Lugworm SVG source(s):`n$($missing -join "`n")"
    }
}

function Assert-Png {
    param([string]$Path, [string]$Name)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing Lugworm PNG output for ${Name}: $Path"
    }
    Add-Type -AssemblyName System.Drawing
    $bitmap = $null
    try {
        $bitmap = [System.Drawing.Bitmap]::new($Path)
        if ($bitmap.Width -ne 24 -or $bitmap.Height -ne 24) {
            throw "Lugworm PNG $Name is $($bitmap.Width)x$($bitmap.Height), expected exactly 24x24."
        }
        if ($bitmap.PixelFormat -ne [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) {
            throw "Lugworm PNG $Name has pixel format $($bitmap.PixelFormat), expected Format32bppArgb."
        }
        $hasVisiblePixel = $false
        $hasTransparentPixel = $false
        for ($y = 0; $y -lt 24 -and (-not ($hasVisiblePixel -and $hasTransparentPixel)); $y++) {
            for ($x = 0; $x -lt 24; $x++) {
                $alpha = $bitmap.GetPixel($x, $y).A
                if ($alpha -gt 0) { $hasVisiblePixel = $true }
                if ($alpha -eq 0) { $hasTransparentPixel = $true }
                if ($hasVisiblePixel -and $hasTransparentPixel) { break }
            }
        }
        if (-not $hasVisiblePixel) { throw "Lugworm PNG $Name contains no visible pixels (alpha > 0)." }
        if (-not $hasTransparentPixel) { throw "Lugworm PNG $Name contains no transparent pixels (alpha = 0)." }
    }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }
}

function Convert-ScreenshotToPng {
    param([string]$InputPath, [string]$OutputPath, [string]$Name)
    Add-Type -AssemblyName System.Drawing
    $source = $null
    $target = $null
    $graphics = $null
    try {
        $source = [System.Drawing.Bitmap]::new($InputPath)
        if ($source.Width -lt 24 -or $source.Height -lt 24) {
            throw "Edge screenshot for $Name is $($source.Width)x$($source.Height), too small to crop to 24x24."
        }
        $target = [System.Drawing.Bitmap]::new(24, 24, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($target)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, 24, 24), [System.Drawing.Rectangle]::new(0, 0, 24, 24), [System.Drawing.GraphicsUnit]::Pixel)
        $target.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $target) { $target.Dispose() }
        if ($null -ne $source) { $source.Dispose() }
    }
    Assert-Png -Path $OutputPath -Name $Name
}

function New-ContactSheet {
    param([object[]]$Icons, [string]$OutputPath)
    Add-Type -AssemblyName System.Drawing
    $columns = 6
    $gap = 6
    $rows = [Math]::Ceiling($Icons.Count / $columns)
    $sheet = [System.Drawing.Bitmap]::new(($columns * 24) + (($columns - 1) * $gap), ($rows * 24) + (($rows - 1) * $gap), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    try {
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#202124'))
        for ($i = 0; $i -lt $Icons.Count; $i++) {
            $bitmap = [System.Drawing.Bitmap]::new($Icons[$i].Output)
            try {
                $x = ($i % $columns) * (24 + $gap)
                $y = [Math]::Floor($i / $columns) * (24 + $gap)
                $graphics.DrawImage($bitmap, $x, $y, 24, 24)
            }
            finally { $bitmap.Dispose() }
        }
        $sheet.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $sheet.Dispose()
    }
}

$tempRoot = $null
try {
    Assert-Manifest
    $icons = @($names | ForEach-Object { Get-IconPaths $_ })
    Assert-Sources

    if ($VerifyOnly) {
        foreach ($icon in $icons) { Assert-Png -Path $icon.Output -Name $icon.Name }
        Write-Output "Lugworm icon verification passed ($($icons.Count) icons)."
        exit 0
    }

    if (-not (Test-Path -LiteralPath $edgePath -PathType Leaf)) {
        throw "Microsoft Edge executable not found at $edgePath"
    }
    if (-not $SkipRasterize) {
        $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot | Out-Null
        foreach ($icon in $icons) {
            $svg = [System.IO.File]::ReadAllText($icon.Source)
            $html = "<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;padding:0;width:24px;height:24px;overflow:hidden;background:transparent}svg{display:block;width:24px;height:24px;filter:drop-shadow(1px 1px 1px rgba(0,0,0,.30))}</style></head><body>$svg</body></html>"
            $htmlPath = Join-Path $tempRoot "$($icon.Name).html"
            $shotPath = Join-Path $tempRoot "$($icon.Name).png"
            [System.IO.File]::WriteAllText($htmlPath, $html, [System.Text.UTF8Encoding]::new($false))
            $uri = ([System.Uri]::new($htmlPath)).AbsoluteUri
            # Edge writes progress ("N bytes written to file ...") and harmless renderer
            # warnings to stderr. Windows PowerShell 5.1 wraps native stderr in an
            # ErrorRecord, which $ErrorActionPreference='Stop' promotes to a TERMINATING
            # error - so the run died on the first icon even though the rasterize
            # succeeded. Relax the preference around the native call only; correctness is
            # still judged by the exit code and Assert-Png (size, pixel format, alpha).
            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & $edgePath '--headless=new' '--disable-gpu' '--disable-crash-reporter' '--no-first-run' '--hide-scrollbars' '--force-device-scale-factor=1' '--default-background-color=00000000' "--screenshot=$shotPath" '--window-size=24,24' $uri 2>$null | Out-Null
            }
            finally { $ErrorActionPreference = $previousEap }
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $shotPath -PathType Leaf)) { throw "Edge failed to rasterize $($icon.Name)." }
            Convert-ScreenshotToPng -InputPath $shotPath -OutputPath $icon.Output -Name $icon.Name
        }
    }

    foreach ($icon in $icons) { Assert-Png -Path $icon.Output -Name $icon.Name }
    New-ContactSheet -Icons $icons -OutputPath (Join-Path $sourceRoot 'contact-sheet.png')
    Write-Output "Lugworm icon export and verification passed ($($icons.Count) icons)."
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
finally {
    if ($null -ne $tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
