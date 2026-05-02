<#
.SYNOPSIS
    Builds ELLAH-ColNum Pro in Release mode and produces a single Setup.exe.
    Run this when you're ready to package a version for distribution.

.OUTPUT
    dist\EllahColNumPro-Setup.exe  — the single file to email/share
#>

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent

Write-Host ""
Write-Host "=== ELLAH-ColNum Pro — Release Build ===" -ForegroundColor Cyan
Write-Host ""

# 1 — Build the Revit plugin
Write-Host "[1/4] Building Revit plugin (Release)..." -ForegroundColor Yellow
dotnet build "$Root\src\Revit\RevitColumnNumberer.Revit.csproj" `
    --configuration Release --nologo 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED." -ForegroundColor Red; exit 1
}
Write-Host "      Plugin built." -ForegroundColor Green

# 2 — Copy plugin files into Setup's Resources folder (so they get embedded)
Write-Host "[2/4] Embedding plugin into installer..." -ForegroundColor Yellow
$resourcesDir = "$Root\src\Setup\Resources"
New-Item -ItemType Directory -Force -Path $resourcesDir | Out-Null
Copy-Item "$Root\src\Revit\bin\Release\EllahColNumPro.dll"      $resourcesDir -Force
Copy-Item "$Root\src\Revit\bin\Release\EllahColNumPro.Core.dll" $resourcesDir -Force
Copy-Item "$Root\src\Revit\EllahColNumPro.addin"                $resourcesDir -Force
Write-Host "      Files embedded." -ForegroundColor Green

# 3 — Publish the Setup project as a single self-contained exe
Write-Host "[3/4] Publishing Setup.exe (self-contained)..." -ForegroundColor Yellow
dotnet publish "$Root\src\Setup\EllahColNumPro.Setup.csproj" `
    --configuration Release `
    --runtime win-x64 `
    --no-self-contained `
    -p:PublishSingleFile=true `
    --output "$Root\dist" `
    --nologo 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish FAILED." -ForegroundColor Red; exit 1
}
Write-Host "      Setup.exe created." -ForegroundColor Green

# 4 — Rename output to versioned name
Write-Host "[4/4] Packaging..." -ForegroundColor Yellow
$date    = Get-Date -Format "yyyy-MM-dd"
$outFile = "$Root\dist\EllahColNumPro-Setup.exe"

if (Test-Path $outFile) {
    Write-Host ""
    Write-Host "=== Done! ===" -ForegroundColor Cyan
    Write-Host "Output: $outFile" -ForegroundColor White
    Write-Host "Size:   $([math]::Round((Get-Item $outFile).Length / 1MB, 1)) MB" -ForegroundColor White
    Write-Host ""
    Write-Host "This single file is ready to email and install on any Windows PC with Revit." -ForegroundColor Green
} else {
    Write-Host "ERROR: Output file not found." -ForegroundColor Red
    exit 1
}

Read-Host "Press Enter to close"
