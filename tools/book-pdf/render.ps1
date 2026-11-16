param(
    [Parameter(Mandatory=$true)][string]$Python,
    [ValidateSet('Pilot','Full')][string]$Edition='Full'
)
$ErrorActionPreference='Stop'
$env:PYTHONIOENCODING='utf-8'
Push-Location $PSScriptRoot
try {
    if (-not (Test-Path -LiteralPath 'tools/pandoc-3.14/pandoc.exe')) { throw 'Restore Pandoc 3.14 Windows x86_64 under tools; verify its archive SHA-256 against versions.json.' }
    & $Python inventory.py
    if ($LASTEXITCODE) {throw 'Source verification failed'}
    if (-not (Test-Path -LiteralPath 'node_modules/@mermaid-js/mermaid-cli/src/cli.js')) {
        $env:PUPPETEER_SKIP_DOWNLOAD='true'
        npm.cmd ci
        if ($LASTEXITCODE) {throw 'npm ci failed'}
    }
    node render-diagrams.mjs
    if ($LASTEXITCODE) {throw 'Diagram rendering failed'}
    & $Python figure-map.py
    if ($LASTEXITCODE) {throw 'Figure mapping failed'}
    & $Python versions.py
    if ($LASTEXITCODE) {throw 'Tool identity recording failed'}
    if (-not (Test-Path -LiteralPath 'lualatex.fmt')) {
        miktex-luahbtex --disable-installer --miktex-disable-maintenance --miktex-disable-diagnose --interaction=nonstopmode --initialize --halt-on-error --alias=lualatex --job-name=lualatex lualatex.ini
        if ($LASTEXITCODE) {throw 'LuaLaTeX format creation failed; install the TeX packages documented in README.txt'}
    }
    & $Python build.py $(if($Edition -eq 'Pilot'){'--pilot'}else{'--full'})
    if ($LASTEXITCODE) {throw 'PDF build failed'}
    & $Python qa.py $(if($Edition -eq 'Pilot'){'pilot'}else{'book'}) --sheets
    if ($LASTEXITCODE) {throw 'PDF inspection failed'}
    if ($Edition -eq 'Full') {
        & $Python additional-checks.py
        if ($LASTEXITCODE) {throw 'Caption/ledger/scope checks failed'}
        & $Python prepare-inspection.py
        if ($LASTEXITCODE) {throw 'Full-resolution page rendering failed'}
    } else {
        & $Python prepare-pilot-inspection.py
        if ($LASTEXITCODE) {throw 'Pilot page rendering failed'}
    }
} finally {Pop-Location}
