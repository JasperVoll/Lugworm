# Rasterizes Lugworm/Resources/icons-src/package-icon.svg to the 64 px
# Food4Rhino package icon at Lugworm/Resources/package-icon.png.
#
# The icon is pure vector in the ribbon-icon design language (no font
# dependency); rendering uses headless Edge exactly like
# icons-src/Lugworm/make_icons.ps1, including the same 1 px drop shadow.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$svgPath = Join-Path $root 'Lugworm\Resources\icons-src\package-icon.svg'
$outPath = Join-Path $root 'Lugworm\Resources\package-icon.png'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path $edge)) { throw "Edge not found at $edge" }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("pkgicon-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    # strip the leading XML comment block so only the <svg> element is embedded
    $svg = [IO.File]::ReadAllText($svgPath)
    $svg = $svg.Substring($svg.IndexOf('<svg'))
    $html = @"
<!doctype html><html><head><meta charset='utf-8'><style>
html,body{margin:0;padding:0;width:64px;height:64px;overflow:hidden;background:transparent}
svg{display:block;width:64px;height:64px;filter:drop-shadow(1px 1px 1px rgba(0,0,0,.30))}
</style></head><body>$svg</body></html>
"@
    $htmlPath = Join-Path $tmp 'icon.html'
    [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))

    # Start-Process instead of direct invocation: Edge writes benign noise to
    # stderr, which Windows PowerShell would turn into a terminating
    # NativeCommandError under ErrorActionPreference=Stop even when redirected
    $edgeArgs = @('--headless=new', '--disable-gpu', '--disable-crash-reporter', '--no-first-run',
                  '--hide-scrollbars', '--force-device-scale-factor=1', '--default-background-color=00000000',
                  '--virtual-time-budget=3000', "--user-data-dir=$tmp\profile",
                  "--screenshot=$outPath", '--window-size=64,64', ('file:///' + $htmlPath.Replace('\', '/')))
    $p = Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -PassThru -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $tmp 'edge-stderr.log')
    if ($p.ExitCode -ne 0) { throw "Edge exited with $($p.ExitCode); see $tmp\edge-stderr.log" }

    Add-Type -AssemblyName System.Drawing
    $img = [System.Drawing.Image]::FromFile($outPath)
    try { if ($img.Width -ne 64 -or $img.Height -ne 64) { throw "render is $($img.Width)x$($img.Height), expected 64x64" } }
    finally { $img.Dispose() }
    Write-Host "wrote $outPath (64x64)"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
