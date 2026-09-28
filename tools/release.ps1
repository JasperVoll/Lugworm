# Lugworm release packaging (AUD-REL-001).
#
# Candidate mode (default): clean Release build, full tests, allowlisted staging
# (Rhino 8 multi-target layout: net48/ + net7.0/), PII/debug scans of every staged
# file AND the finished archives, the installable .yak ("yak build --platform win",
# skipped with a warning when Rhino 8's Yak.exe is absent), then
# dist/send-Lugworm-<version>.zip carrying that .yak next to the raw payload,
# and a git-archive-based source ZIP restricted to tools/public-paths.txt. The
# source ZIP is not a release asset: tools/publish-source.ps1 imports it into the
# public repository. Produces local artifacts only - nothing is published and no
# URL is required.
#
# Public mode (-Mode Public): additionally requires a clean tagged tree, a real
# HTTPS release URL, an explicit confirmation that the owner-only manual gates
# have passed, and Yak.exe (the .yak is mandatory).
#
# Usage:
#   powershell -File tools/release.ps1                 # candidate
#   powershell -File tools/release.ps1 -Mode Public -ReleaseUrl https://... -OwnerGatesConfirmed
param(
    [ValidateSet('Candidate', 'Public')] [string]$Mode = 'Candidate',
    [string]$ReleaseUrl = '',
    [switch]$OwnerGatesConfirmed,
    [string]$SkipTests = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Fail([string]$msg) { Write-Host "RELEASE FAILED: $msg" -ForegroundColor Red; exit 1 }

# Native dotnet diagnostics otherwise disappear behind Out-Null (and stderr can
# become a terminating PowerShell error). Keep the gate strict, but retain the
# complete merged output so an operator can see restore/compiler/test failures.
function Invoke-Dotnet([string[]]$dotnetArgs, [string]$label) {
    $log = @(& dotnet @dotnetArgs 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Write-Host "--- $label diagnostics ---" -ForegroundColor Yellow
        $log | ForEach-Object { Write-Host ([string]$_) }
        Write-Host "--- end $label diagnostics ---" -ForegroundColor Yellow
        Fail "$label failed (dotnet exit code $exitCode)"
    }
    return $log
}

# ---- version: single source of truth is <Version> in Lugworm.csproj ----
$csproj = Join-Path $root 'Lugworm\Lugworm.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "no <Version> in $csproj" }
# Core ships beside the plugin and carries its own <Version>; the two must not drift.
$coreCsproj = Join-Path $root 'Lugworm.Core\Lugworm.Core.csproj'
$coreVersion = ([xml](Get-Content $coreCsproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($coreVersion -cne $version) { Fail "Lugworm.Core.csproj <Version> ($coreVersion) must equal Lugworm.csproj <Version> ($version)" }
Write-Host "version $version ($Mode mode)"

# ---- mode gates ----
$dirty = git status --porcelain
if ($Mode -eq 'Public') {
    if ($dirty) { Fail 'working tree is dirty - commit or stash before a public release' }
    $tag = git tag --points-at HEAD | Where-Object { $_ -ceq "v$version" }
    if (-not $tag) { Fail "HEAD is not tagged v$version - tag the release commit first" }
    if ($ReleaseUrl -notmatch '^https://') { Fail 'public mode requires -ReleaseUrl https://... (the Food4Rhino/project page)' }
    if (-not $OwnerGatesConfirmed) { Fail 'public mode requires -OwnerGatesConfirmed after the Rhino/hardware/manual release gates pass' }
}
elseif ($dirty) { Write-Host 'note: working tree is dirty (allowed in candidate mode)' -ForegroundColor Yellow }

# ---- clean Release build + tests ----
Invoke-Dotnet @('clean', 'Lugworm.sln', '-c', 'Release', '-v', 'q', '-m:1') 'Release clean' | Out-Null
$buildArgs = @('build', 'Lugworm.sln', '-c', 'Release', '-m:1')
$buildLog = Invoke-Dotnet $buildArgs 'Release build'
$warnings = @($buildLog | Where-Object { $_ -match '(?m)^\s*([1-9]\d*) Warning' })
if ($warnings) {
    Write-Host '--- Release build warnings (warnings are fatal for packaging) ---' -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host ([string]$_) }
    Write-Host '--- end Release build warnings ---' -ForegroundColor Yellow
    Fail 'Release build produced warnings'
}
if ($SkipTests -ne 'yes') {
    # Equivalent command: dotnet test Lugworm.sln -c Release --no-build --no-restore
    Invoke-Dotnet @('test', 'Lugworm.sln', '-c', 'Release', '--no-build', '--no-restore', '-m:1') 'Release tests' | Out-Null
    Write-Host 'tests green'
}

# ---- staging: allowlist only, Rhino 8 multi-target layout net48/ + net7.0/ ----
# net7.0-windows is a build/test gate but is NOT duplicated in the package.
$stage = Join-Path $root "dist\stage-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$payload = @('Lugworm.gha', 'Lugworm.Core.dll', 'Clipper2Lib.dll', 'MathNet.Numerics.dll',
             'SkiaSharp.dll', 'SkiaSharp.Views.Desktop.Common.dll', 'SkiaSharp.Views.WindowsForms.dll',
             'OpenTK.dll', 'OpenTK.GLControl.dll')
# The Skia native is placed differently per TFM and BOTH layouts are load-bearing:
# net48's loader probes x64\ beside the managed dll, while the netcore folders need it
# at the assembly root - with the usual runtimes/ layout the process loaded a wrong
# libSkiaSharp.dll from Git's mingw64\bin off PATH. Assert the layout rather than
# trusting it: an allowlist that silently drops the native ships a console that
# cannot paint.
$nativeRelative = @{ 'net48' = 'x64\libSkiaSharp.dll'; 'net7.0' = 'libSkiaSharp.dll' }
foreach ($tfm in 'net48', 'net7.0') {
    $src = Join-Path $root "Lugworm\bin\Release\$tfm"
    $dst = Join-Path $stage $tfm
    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($f in $payload) {
        $p = Join-Path $src $f
        if (-not (Test-Path $p)) { Fail "expected payload file missing: $p" }
        Copy-Item $p $dst
    }
    $rel = $nativeRelative[$tfm]
    $nativeSrc = Join-Path $src $rel
    if (-not (Test-Path $nativeSrc)) { Fail "expected Skia native missing: $nativeSrc" }
    $nativeDst = Join-Path $dst $rel
    New-Item -ItemType Directory -Force (Split-Path $nativeDst -Parent) | Out-Null
    Copy-Item $nativeSrc $nativeDst
    if (-not (Test-Path $nativeDst)) { Fail "Skia native did not survive staging: $nativeDst" }
    # Presence is NOT enough. The package once shipped the 32-bit libSkiaSharp because all
    # three RID copies share a file name; Rhino 8 is x64, so the console could not paint.
    # Read the PE machine field and refuse anything that is not x64.
    $fs = [IO.File]::OpenRead($nativeDst)
    try {
        $br = New-Object IO.BinaryReader($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { Fail "not a PE image: $nativeDst" }
        $fs.Position = 0x3C
        $fs.Position = $br.ReadInt32()
        if ($br.ReadUInt32() -ne 0x00004550) { Fail "not a PE image: $nativeDst" }
        $machine = $br.ReadUInt16()
    } finally { $fs.Dispose() }
    if ($machine -ne 0x8664) {
        $name = switch ($machine) { 0x14C { 'x86' } 0xAA64 { 'arm64' } default { "0x{0:X}" -f $machine } }
        Fail "staged Skia native is $name, expected x64: $nativeDst"
    }
    # A macOS native or a runtimes/ tree in a Windows-only package means the csproj
    # placement targets regressed.
    if (Get-ChildItem $dst -Recurse -Include '*.dylib', '*.so' -ErrorAction SilentlyContinue) {
        Fail "non-Windows native staged for $tfm"
    }
}
Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage

# ---- upstream license/notice files of the pinned Skia/OpenTK packages, verbatim ----
# The root THIRD-PARTY-NOTICES.md names the licenses; the packages' own LICENSE and
# THIRD-PARTY-NOTICES files are redistribution notices and travel with the binaries.
# Copied from the restored NuGet cache with the package hash recorded, never edited.
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$licensePackages = @(
    @{ Id = 'SkiaSharp'; Version = '2.88.6' },
    @{ Id = 'SkiaSharp.NativeAssets.Win32'; Version = '2.88.6' },
    @{ Id = 'SkiaSharp.Views.Desktop.Common'; Version = '2.88.6' },
    @{ Id = 'SkiaSharp.Views.WindowsForms'; Version = '2.88.6' },
    @{ Id = 'OpenTK'; Version = '3.1.0' },
    @{ Id = 'OpenTK.GLControl'; Version = '3.1.0' }
)
$licenseDir = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $licenseDir | Out-Null
$provenance = @('Upstream license and notice files copied verbatim from the pinned NuGet packages (tools/release.ps1).', '')
foreach ($pkg in $licensePackages) {
    $pkgDir = Join-Path $nugetRoot ("{0}\{1}" -f $pkg.Id.ToLowerInvariant(), $pkg.Version)
    if (-not (Test-Path $pkgDir)) { Fail "pinned package is not in the NuGet cache (restore first): $($pkg.Id) $($pkg.Version)" }
    $target = Join-Path $licenseDir ("{0}-{1}" -f $pkg.Id, $pkg.Version)
    New-Item -ItemType Directory -Force $target | Out-Null
    $files = @(Get-ChildItem $pkgDir -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })
    foreach ($lf in $files) { Copy-Item $lf.FullName $target }
    $shaFile = Join-Path $pkgDir ("{0}.{1}.nupkg.sha512" -f $pkg.Id.ToLowerInvariant(), $pkg.Version)
    $sha = if (Test-Path $shaFile) { (Get-Content $shaFile -Raw).Trim() } else { 'sha512 file missing' }
    $names = if ($files) { ($files | ForEach-Object { $_.Name }) -join ', ' } else { 'none in the package (MIT; see THIRD-PARTY-NOTICES.md)' }
    $provenance += "$($pkg.Id) $($pkg.Version): nupkg sha512 (base64) $sha; files: $names"
}
if (-not (Test-Path (Join-Path $licenseDir 'SkiaSharp.NativeAssets.Win32-2.88.6\LICENSE.txt'))) { Fail 'Skia native LICENSE.txt did not land in the package' }
[IO.File]::WriteAllText((Join-Path $licenseDir 'PROVENANCE.txt'), (($provenance -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

# ---- build identity: what produced this package (not a claim of byte-identical rebuilds) ----
$commit = (& git -C $root rev-parse HEAD).Trim()
$sdk = (& dotnet --version).Trim()
$refs = @(Select-String -Path (Join-Path $root 'Lugworm\Lugworm.csproj'), (Join-Path $root 'Lugworm.Core\Lugworm.Core.csproj') `
            -Pattern 'PackageReference Include="([^"]+)" Version="([^"]+)"' |
          ForEach-Object { "  $($_.Matches[0].Groups[1].Value) $($_.Matches[0].Groups[2].Value)" } | Sort-Object -Unique)
$buildInfo = @("Lugworm $version", "commit: $commit", "dotnet sdk: $sdk",
               "packaged: $(Get-Date -Format o)",
               'note: the assembly build date is the build clock; this source commit and SDK rebuild the same code, not byte-identical binaries',
               'package references (direct; shipped transitive packages such as OpenTK are recorded with hashes in licenses/PROVENANCE.txt):') + $refs
[IO.File]::WriteAllText((Join-Path $stage 'BUILD-INFO.txt'), (($buildInfo -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$icon = Join-Path $root 'Lugworm\Resources\package-icon.png'
if (-not (Test-Path $icon)) { Fail 'required Food4Rhino package icon is missing: Lugworm\Resources\package-icon.png' }
Copy-Item $icon $stage

# curated manifest (never yak spec over raw build output)
$manifest = @(
    'name: lugworm'
    "version: $version"
    'authors:'
    '- Jasper Vollmert - IDAC, Hamburg University of Technology'
    'description: Slicer, G-code generator, Duet console and robot tools for paste extrusion in Grasshopper.'
)
if ($ReleaseUrl) { $manifest += "url: $ReleaseUrl" }
if (Test-Path $icon) { $manifest += 'icon: package-icon.png' }
$manifest += 'keywords:'
$manifest += @('- clay', '- ldm', '- paste', '- 3dprinting', '- gcode', '- slicer', '- robot', '- duet', '- grasshopper')
# BOM-less UTF-8: Windows PowerShell's -Encoding utf8 writes a BOM, which YAML
# parsers may reject as the first bytes of the document
[IO.File]::WriteAllText((Join-Path $stage 'manifest.yml'), (($manifest -join "`n") + "`n"),
                        [Text.UTF8Encoding]::new($false))

# ---- PII / debug scan: ASCII AND UTF-16, staged files and later the archives ----
# Literal terms (escaped): private identifiers that must never ship. The author's name
# is deliberate attribution (README, CITATION, LICENSE) and is not a term.
$scanTerms = @('cjv' + '4306', '01_' + 'IDAC', 'Programming\\' + 'IAMLabSlicer', 'Programming\\' + 'Lugworm',
               'awj' + '19', 'Next' + 'cloud', 'collaborating.' + 'tuhh.de', 'tu-' + 'harburg', 'h5a-' + 'nbk-185')
# Regex patterns (not escaped): user-profile paths in any spelling (C:\Users, C:\\Users in
# JSON, C:/Users), private LAN addresses and mDNS names. The 10/8 class is left out: four-part
# version numbers such as 10.0.26200.0 would match it.
$scanPatterns = @('[A-Za-z]:[\\/]+Users[\\/]',
                  '\b192\.168\.\d{1,3}\.\d{1,3}\b',
                  '\b172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}\b',
                  '(?-i)\b[\w-]+\.local\b')
$debugTerms = @('\bin\' + 'Debug\', '/bin/' + 'Debug/')
# Allowed: attribution, and the placeholder hosts used by the docs, tooltips and tests
# (192.168.1.14 in the recorded console clip is a placeholder as well, owner decision 2026-09-23).
$allowTerms = @('Jasper Vollmert - IDAC', 'Copyright (c) 2026 Jasper Vollmert', 'jasper.vollmert@tuhh.de',
                '192.168.1.42', '192.168.1.50', '192.168.1.77', '192.168.1.10', '192.168.1.14', '192.168.1.1',
                'printer.local')
function Test-Bytes([byte[]]$bytes, [string]$name, [switch]$TextDoc) {
    foreach ($enc in @([Text.Encoding]::GetEncoding(28591), [Text.Encoding]::Unicode,
                       [Text.Encoding]::BigEndianUnicode)) {
        $text = $enc.GetString($bytes)
        foreach ($allow in $allowTerms) { $text = $text.Replace($allow, '') }
        foreach ($term in $scanTerms) {
            if ($text -match [regex]::Escape($term).Replace('\\\\', '\\')) {
                Fail "PII scan hit '$term' in $name"
            }
        }
        foreach ($pattern in $scanPatterns) {
            if ($text -match $pattern) {
                Fail "PII pattern '$pattern' matched '$($Matches[0])' in $name"
            }
        }
        # Debug-path markers catch build output; a Markdown page of the source export may
        # legitimately describe the bin\Debug folder (RUNNING_IN_VS.md), so text docs skip it.
        if (-not $TextDoc) {
            foreach ($term in $debugTerms) {
                if ($text.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    Fail "Debug artifact marker '$term' in $name"
                }
            }
        }
    }
}
$scanFiles = @(Get-ChildItem $stage -Recurse -File)
$scanFiles += Get-Item (Join-Path $root 'Lugworm\Resources\robot.xml')
$scanFiles += Get-Item (Join-Path $root 'Lugworm\Resources\Robot_Assembly.3dm')
foreach ($f in $scanFiles) {
    if ($f.Extension -in '.pdb') { Fail "PDB in stage: $($f.FullName)" }
    Test-Bytes ([IO.File]::ReadAllBytes($f.FullName)) $f.Name
}
$staged = Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.Extension.ToLowerInvariant() } | Sort-Object -Unique
$allowedExt = @('.gha', '.dll', '.md', '.yml', '.png', '.txt', '')
foreach ($e in $staged) { if ($e -notin $allowedExt) { Fail "unexpected file type in stage: $e" } }
Write-Host 'stage scan clean'

# ---- yak: the installable package (final test round 1.1: the send zip carried
# only raw build output, which "yak install" cannot consume). Built from the scanned
# stage in both modes with the win platform tag (the plugin is Windows-only);
# candidate mode warns and continues without Rhino 8, public mode fails. ----
New-Item -ItemType Directory -Force (Join-Path $root 'dist') | Out-Null
$yakDist = $null
$yak = 'C:\Program Files\Rhino 8\System\Yak.exe'
if (Test-Path $yak) {
    Push-Location $stage
    try { & $yak build --platform win; if ($LASTEXITCODE -ne 0) { Fail 'yak build failed' } }
    finally { Pop-Location }
    $yakFile = Get-ChildItem $stage -Filter '*.yak' | Select-Object -First 1
    if (-not $yakFile) { Fail 'yak build produced no .yak in the stage' }
    $yakDist = Join-Path $root "dist\$($yakFile.Name)"
    Move-Item $yakFile.FullName $yakDist -Force
    Write-Host "yak package:     dist\$($yakFile.Name)"
}
elseif ($Mode -eq 'Public') { Fail 'Yak.exe not found - install Rhino 8 on this machine' }
else { Write-Host 'warning: Yak.exe not found - the candidate zip ships WITHOUT the .yak (install Rhino 8 to build it)' -ForegroundColor Yellow }

# ---- archives: the send zip = the .yak (what INSTALL.md installs) + the raw
# multi-target payload for manual installs + INSTALL.md. The doc is added to the
# zip, not to the stage, so it stays out of the .yak payload. Updating an existing
# installation is covered by the installation page of the site. ----
$zip = Join-Path $root "dist\send-Lugworm-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$installDoc = Join-Path $root 'INSTALL.md'
if (-not (Test-Path $installDoc)) { Fail 'the send zip needs INSTALL.md at the repository root' }
$zipInputs = @((Join-Path $stage '*'), $installDoc)
if ($yakDist) { $zipInputs += $yakDist }
Compress-Archive -Path $zipInputs -DestinationPath $zip
# verify the FINISHED archive, not just the staging folder
Add-Type -AssemblyName System.IO.Compression.FileSystem
$za = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $za.Entries) {
        $ms = New-Object IO.MemoryStream
        $entry.Open().CopyTo($ms)
        Test-Bytes $ms.ToArray() "zip:$($entry.FullName)"
        if ($entry.FullName -match '\.pdb$|\.deps\.json$|runtimeconfig') { Fail "forbidden file in archive: $($entry.FullName)" }
    }
}
finally { $za.Dispose() }
Write-Host ("binary package: $zip" + $(if ($yakDist) { ' (with ' + (Split-Path $yakDist -Leaf) + ')' } else { ' (no .yak)' }))

# ---- clean source export from committed content only (AUD-REL-003/004) ----
$srcZip = Join-Path $root "dist\source-Lugworm-$version.zip"
if (Test-Path $srcZip) { Remove-Item $srcZip -Force }
# The allowlist lives in tools/public-paths.txt: tools/publish-source.ps1 (which
# replaces exactly these paths in the public checkout) and the tooling tests share it.
$allowlistFile = Join-Path $root 'tools\public-paths.txt'
if (-not (Test-Path $allowlistFile)) { Fail "public source allowlist is missing: $allowlistFile" }
$publicPaths = @(Get-Content $allowlistFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
if (-not $publicPaths) { Fail 'public source allowlist is empty' }
if ($publicPaths -contains '.gitignore') { Fail 'the private .gitignore must not be exported (it names internal folders)' }
foreach ($publicPath in $publicPaths) {
    git cat-file -e "HEAD`:$publicPath" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Fail "public source path is not committed: $publicPath"
    }
}
git archive --format=zip -o $srcZip HEAD -- @publicPaths
if ($LASTEXITCODE -ne 0) { Fail 'git archive failed' }
# The source export is an artifact too. Scan the finished ZIP entry-by-entry so
# UTF-16 paths/usernames hidden in binary resources cannot bypass the source gate.
$sourceZa = [IO.Compression.ZipFile]::OpenRead($srcZip)
try {
    foreach ($entry in $sourceZa.Entries) {
        $ms = New-Object IO.MemoryStream
        $entry.Open().CopyTo($ms)
        Test-Bytes $ms.ToArray() "source-zip:$($entry.FullName)" -TextDoc:($entry.FullName -match '\.md$')
        if ($entry.FullName -match '(^|/)(bin|obj|dist)/|\.patched$|\.pdb$|\.user$|\.suo$') {
            Fail "forbidden file in source archive: $($entry.FullName)"
        }
    }
}
finally { $sourceZa.Dispose() }
Write-Host "source package:  $srcZip (committed content only - internal docs, history and ignored files excluded)"

# The source export is not a release asset: the public repository carries the code and
# tools/publish-source.ps1 imports this ZIP into it. Record which commit produced it so
# the import can refuse a stale export.
$srcHash = (Get-FileHash $srcZip -Algorithm SHA256).Hash
$srcRecord = Join-Path $root "dist\SOURCE-EXPORT-$version.txt"
[IO.File]::WriteAllText($srcRecord, ((@("commit: $commit", "sha256: $srcHash", "file: $(Split-Path $srcZip -Leaf)") -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "source record:   $srcRecord (import with tools/publish-source.ps1)"

# ---- artifact hashes of the release assets, so the release page can name exactly these files ----
$hashLines = @("Lugworm $version - SHA-256 of the release artifacts (commit $commit)")
foreach ($artifact in @($zip, $yakDist)) {
    if ($artifact -and (Test-Path $artifact)) {
        $h = (Get-FileHash $artifact -Algorithm SHA256).Hash
        $hashLines += "$h  $(Split-Path $artifact -Leaf)"
        Write-Host "  $h  $(Split-Path $artifact -Leaf)"
    }
}
$hashFile = Join-Path $root "dist\HASHES-$version.txt"
[IO.File]::WriteAllText($hashFile, (($hashLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "hashes:          $hashFile"

Write-Host 'DONE. Nothing was uploaded or published.' -ForegroundColor Green
