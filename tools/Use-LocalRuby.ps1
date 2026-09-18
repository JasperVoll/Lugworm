# Activate the optional, ignored runtime stored under this site folder.
$siteRoot = Split-Path $PSScriptRoot -Parent
$rubyBin = Join-Path $siteRoot '.tools\rubyinstaller-3.2.8-1-x64\bin'
if (-not (Test-Path -LiteralPath (Join-Path $rubyBin 'ruby.exe'))) {
    throw 'The optional local runtime is absent. Install Ruby+Devkit as described in SETUP.md.'
}
$env:GEM_HOME = Join-Path $siteRoot '.tools\gems'
$env:GEM_PATH = $env:GEM_HOME
$env:BUNDLE_USER_HOME = Join-Path $siteRoot '.tools\bundle-user'
$env:TEMP = Join-Path $siteRoot '.tools\tmp'
$env:TMP = $env:TEMP
$env:RI_DEVKIT = Join-Path $siteRoot '.tools\msys64'
$env:PATH = $rubyBin + ';' + (Join-Path $env:GEM_HOME 'bin') + ';' + $env:PATH
