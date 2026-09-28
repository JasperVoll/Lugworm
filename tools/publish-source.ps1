# Lugworm public source import.
#
# Copies the scanned source export built by tools/release.ps1
# (dist/source-Lugworm-<version>.zip) into the public GitHub checkout and commits it
# there as one import commit. This private repository stays the development
# authority and its history never leaves. The paths listed in tools/public-paths.txt
# are owned by this tree and replaced on every import; everything else in the public
# checkout (site/, .github/, the public .gitignore) is left untouched. A "!path" line
# keeps a path below an exported directory private, so the import removes it there.
#
# The export is trusted only when dist/SOURCE-EXPORT-<version>.txt says it was built
# from the current HEAD and its SHA-256 still matches, so the public commit always
# corresponds to one committed private state that passed the release scan.
#
# The script never pushes. Read the printed diff, then push the branch and the tag
# yourself.
#
# Usage:
#   powershell -File tools/publish-source.ps1                        # import into docs/github on its current branch
#   powershell -File tools/publish-source.ps1 -Tag                   # also create the annotated tag v<version>
#   powershell -File tools/publish-source.ps1 -PublicCheckout D:\pub # another checkout of the public repository
param(
    [string]$PublicCheckout = '',
    [switch]$Tag
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Fail([string]$msg) { Write-Host "IMPORT FAILED: $msg" -ForegroundColor Red; exit 1 }

# stderr of git is left on the console on purpose: redirecting it in Windows
# PowerShell turns every line into an error record.
function Invoke-Git([string]$dir, [string[]]$gitArgs) {
    & git -C $dir @gitArgs
    if ($LASTEXITCODE -ne 0) { Fail "git $($gitArgs -join ' ') failed in $dir" }
}

function Read-AllowlistEntries([string]$file) {
    if (-not (Test-Path $file)) { Fail "public source allowlist is missing: $file" }
    return @(Get-Content $file | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}

function Read-Allowlist([string[]]$entries) {
    $paths = @($entries | Where-Object { -not $_.StartsWith('!') })
    if (-not $paths) { Fail 'public source allowlist is empty' }
    if ($paths -contains '.gitignore') { Fail 'the private .gitignore must not be exported (it names internal folders)' }
    return $paths
}

# "!path" lines: paths below an exported directory that stay private.
function Read-PrivateList([string[]]$entries) {
    return @($entries | Where-Object { $_.StartsWith('!') } | ForEach-Object { $_.Substring(1) })
}

function Test-ManagedPath([string]$rel, [string[]]$paths) {
    foreach ($p in $paths) {
        if ($rel -eq $p -or $rel.StartsWith($p + '/')) { return $true }
    }
    return $false
}

# ---- version and export identity ----
$csproj = Join-Path $root 'Lugworm\Lugworm.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "no <Version> in $csproj" }

if (git -C $root status --porcelain --untracked-files=no) {
    Fail 'the private working tree has uncommitted changes - commit first, then rebuild the export with tools/release.ps1'
}
$head = (git -C $root rev-parse HEAD).Trim()

$srcZip = Join-Path $root "dist\source-Lugworm-$version.zip"
$srcRecord = Join-Path $root "dist\SOURCE-EXPORT-$version.txt"
if (-not (Test-Path $srcZip)) { Fail "source export is missing: $srcZip (run tools/release.ps1 first)" }
if (-not (Test-Path $srcRecord)) { Fail "source export record is missing: $srcRecord (run tools/release.ps1 first)" }
$record = @{}
Get-Content $srcRecord | ForEach-Object { if ($_ -match '^(\w+):\s*(.+)$') { $record[$Matches[1]] = $Matches[2].Trim() } }
if ($record['commit'] -ne $head) {
    Fail "the export was built from commit $($record['commit']) but HEAD is $head - rerun tools/release.ps1"
}
$srcHash = (Get-FileHash $srcZip -Algorithm SHA256).Hash
if ($srcHash -ne $record['sha256']) {
    Fail "the export ZIP does not match its record ($srcHash vs $($record['sha256'])) - rerun tools/release.ps1"
}

$allowEntries = Read-AllowlistEntries (Join-Path $root 'tools\public-paths.txt')
$publicPaths = Read-Allowlist $allowEntries
$privatePaths = Read-PrivateList $allowEntries

# ---- public checkout ----
if (-not $PublicCheckout) { $PublicCheckout = Join-Path $root 'docs\github' }
if (-not (Test-Path $PublicCheckout)) { Fail "public checkout not found: $PublicCheckout" }
$pub = (Resolve-Path $PublicCheckout).Path.TrimEnd('\')
if (-not (Test-Path (Join-Path $pub '.git'))) { Fail "$pub is not the root of a git checkout" }
$pubTop = (git -C $pub rev-parse --show-toplevel).Trim()
$rootTop = (git -C $root rev-parse --show-toplevel).Trim()
if ($pubTop -eq $rootTop) { Fail 'the public checkout is this repository' }
$branch = (git -C $pub rev-parse --abbrev-ref HEAD).Trim()
if ($branch -eq 'HEAD') { Fail 'the public checkout is in detached HEAD state - check out a branch first' }
if (git -C $pub status --porcelain --untracked-files=no) {
    Fail "the public checkout ($branch) has uncommitted changes to tracked files - commit or stash them first"
}
foreach ($p in $publicPaths) {
    $untracked = @(git -C $pub ls-files --others --exclude-standard -- $p)
    if ($untracked) { Fail "untracked files under managed path '$p' in the public checkout: $($untracked -join ', ')" }
    $ignored = @(git -C $pub ls-files --others --ignored --exclude-standard -- $p)
    $unexpected = @($ignored | Where-Object { $_ -notmatch '(^|/)(bin|obj|__pycache__)/' })
    if ($unexpected) { Fail "ignored files under managed path '$p' would be deleted: $($unexpected -join ', ') - move them out first" }
}

Write-Host "import Lugworm $version into $pub [$branch]"

# ---- replace the managed paths with the export ----
foreach ($p in $publicPaths) {
    if (@(git -C $pub ls-files -- $p)) { Invoke-Git $pub @('rm', '-r', '-q', '--', $p) }
    $full = Join-Path $pub ($p -replace '/', '\')
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$za = [IO.Compression.ZipFile]::OpenRead($srcZip)
$count = 0
try {
    foreach ($entry in $za.Entries) {
        $name = $entry.FullName
        if ($name.EndsWith('/')) { continue }
        if ($name.StartsWith('/') -or $name -match '^[A-Za-z]:' -or $name -match '(^|/)\.\.(/|$)') { Fail "unsafe path in export: $name" }
        if (-not (Test-ManagedPath $name $publicPaths)) { Fail "export entry outside the allowlist: $name" }
        if ($privatePaths -and (Test-ManagedPath $name $privatePaths)) { Fail "export entry on the private list: $name - rerun tools/release.ps1" }
        $dest = Join-Path $pub ($name -replace '/', '\')
        $dir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        $count++
    }
}
finally { $za.Dispose() }
Write-Host "extracted $count files from $(Split-Path $srcZip -Leaf)"

# ---- stage; refuse silently dropped files ----
foreach ($p in $publicPaths) {
    if (Test-Path -LiteralPath (Join-Path $pub ($p -replace '/', '\'))) { Invoke-Git $pub @('add', '-A', '--', $p) }
}
$dropped = @(git -C $pub ls-files --others --ignored --exclude-standard -- $publicPaths |
             Where-Object { $_ -notmatch '(^|/)(bin|obj|__pycache__)/' })
if ($dropped) { Fail "the public .gitignore hides exported files: $($dropped -join ', ')" }

& git -C $pub diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Host "nothing to import: the public checkout already matches the export of $version" -ForegroundColor Yellow
    exit 0
}
$stat = @(git -C $pub diff --cached --stat)
Invoke-Git $pub @('commit', '-q', '-m', "source: import Lugworm $version")
$sha = (git -C $pub rev-parse --short HEAD).Trim()
Write-Host "committed $sha on $branch"
$stat | ForEach-Object { Write-Host ([string]$_) }

if ($Tag) {
    $tagName = "v$version"
    if (@(git -C $pub tag --list $tagName)) { Fail "tag $tagName already exists in the public checkout - delete it deliberately before re-tagging" }
    Invoke-Git $pub @('tag', '-a', $tagName, '-m', "Lugworm $version")
    Write-Host "tagged $tagName"
}

Write-Host "DONE. Nothing was pushed. Review: git -C `"$pub`" show --stat HEAD" -ForegroundColor Green
