<#
  Builds the portable Windows release of HDLSS5:
    dist\HDLSS5-Portable-<version>\HDLSS5.exe   self-contained, no .NET install needed
                                  \bundled\      RenoDX NR add-ons + UE4SS reader (hash-checked at use)
                                  \nr-packages\  NR 310.9.16.0 models (RTX 4000 / RTX 5000)
                                  \README.txt
    dist\HDLSS5-Portable-<version>.zip
  Usage:  .\build-portable.ps1 [-NrPackages <folder>] [-NoZip] [-SkipTests]
#>
param(
    [string]$NrPackages = (Join-Path $PSScriptRoot '..\nr-packages'),
    [switch]$NoZip,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
$name = "HDLSS5-Portable-$version"
$dist = Join-Path $PSScriptRoot 'dist'
$out = Join-Path $dist $name

# The bundled NR models must match the hashes HDLSS5 verifies at install time.
$models = @{
    'rtx40\nvngx_dlssnr.dll' = 'cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed'
    'rtx50\nvngx_dlssnr.dll' = '2bc19bc09a7b89dd44f73e2ac0108fcc586e2154bdd0830f242f4f040e28789f'
}
foreach ($m in $models.GetEnumerator()) {
    $file = Join-Path $NrPackages $m.Key
    if (-not (Test-Path $file)) { throw "Modello mancante: $file (usa -NrPackages)" }
    if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLower() -ne $m.Value) { throw "Hash diverso per $file" }
}

if (-not $SkipTests) {
    Write-Host '== Test' -ForegroundColor Cyan
    dotnet test (Join-Path $PSScriptRoot 'tests\Hdlss.Core.Tests') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Test non superati: pacchetto non creato.' }
}

Write-Host "== Pubblicazione $name" -ForegroundColor Cyan
# If the app was run straight from dist, keep its data (settings, backups, history) out of the
# rebuild and out of the zip, then put it back.
$keptData = $null
foreach ($dataDir in 'data-v2', 'data-location.txt') {
    $existing = Join-Path $out $dataDir
    if (Test-Path $existing) {
        if (-not $keptData) { $keptData = Join-Path $dist ('_kept-' + [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory $keptData | Out-Null }
        Move-Item $existing $keptData
    }
}
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot 'src\Hdlss.App') -c Release -r win-x64 --self-contained true --nologo -v q `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'Pubblicazione non riuscita.' }

Write-Host '== Modelli NR' -ForegroundColor Cyan
foreach ($m in $models.Keys) {
    $dest = Join-Path $out "nr-packages\$m"
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $NrPackages $m) $dest
}

Copy-Item (Join-Path $PSScriptRoot 'README.txt') $out
foreach ($required in 'HDLSS5.exe', 'bundled\renodx-dlss5-lecram-10.8.addon64', 'bundled\renodx-dlss5-shortfuse.addon64', 'bundled\hdlss-telemetry.lua') {
    if (-not (Test-Path (Join-Path $out $required))) { throw "Manca nel pacchetto: $required" }
}

# Checksums so a user can verify the download.
Get-ChildItem $out -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.FullName.Substring($out.Length + 1)
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8

if (-not $NoZip) {
    Write-Host '== Archivio zip' -ForegroundColor Cyan
    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    '{0}  {1}' -f (Get-FileHash $zip).Hash.ToLower(), (Split-Path $zip -Leaf) | Set-Content "$zip.sha256" -Encoding utf8
}

if ($keptData) {
    Get-ChildItem $keptData | Move-Item -Destination $out
    Remove-Item $keptData
    Write-Host 'Dati esistenti (data-v2) conservati nella cartella portatile, esclusi dallo zip.' -ForegroundColor Yellow
}

Write-Host "Pronto: $out" -ForegroundColor Green
