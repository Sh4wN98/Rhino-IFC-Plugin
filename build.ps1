# Baut das Plugin und das Yak-Paket für Rhino 8.
#   .\build.ps1                      -> dist\  (RhinoIfc.rhp, RhinoIfc.Core.dll, manifest.yml) + .yak
#   .\build.ps1 -Yak "E:\Rhino\System\Yak.exe"
param(
    [string]$Configuration = "Release",
    [string]$Yak = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

dotnet test (Join-Path $root "tests\RhinoIfc.Core.Tests") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen" }
dotnet build (Join-Path $root "src\RhinoIfc\RhinoIfc.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen" }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null
$bin = Join-Path $root "src\RhinoIfc\bin\$Configuration\net48"
Copy-Item (Join-Path $bin "RhinoIfc.rhp"), (Join-Path $bin "RhinoIfc.Core.dll") $dist
Copy-Item (Join-Path $root "yak\manifest.yml"), (Join-Path $root "LICENSE") $dist

if (-not $Yak) {
    # Rhino 8 bringt Yak mit; zuerst die Installation aus der Registry, dann der Standardordner.
    $install = (Get-ItemProperty "HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install" -ErrorAction SilentlyContinue).Path
    foreach ($candidate in @(
        $(if ($install) { Join-Path $install "Yak.exe" }),
        "C:\Program Files\Rhino 8\System\Yak.exe")) {
        if ($candidate -and (Test-Path $candidate)) { $Yak = $candidate; break }
    }
}
if ($Yak -and (Test-Path $Yak)) {
    Push-Location $dist
    try {
        & $Yak build --platform win
        if ($LASTEXITCODE -ne 0) { throw "yak build fehlgeschlagen" }
    } finally { Pop-Location }
    Get-ChildItem $dist -Filter *.yak | ForEach-Object { Write-Host "Yak-Paket: $($_.FullName)" }
} else {
    Write-Host "Yak.exe nicht gefunden: dist\ enthält das Plugin, aber kein .yak (Pfad mit -Yak angeben)."
}
