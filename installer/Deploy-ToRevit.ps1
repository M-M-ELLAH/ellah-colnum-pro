<#
.SYNOPSIS
    Builds and deploys ELLAH-ColNum Pro to the Revit 2027 Addins folder.
    Run this script every time you want to test a new version in Revit.

.USAGE
    Right-click the script → "Run with PowerShell"
    OR from terminal: .\installer\Deploy-ToRevit.ps1
#>

$ErrorActionPreference = "Stop"

# ── Configuration ────────────────────────────────────────────────────────────
$RevitVersion  = "2027"
$ProjectPath   = "$PSScriptRoot\..\src\Revit\RevitColumnNumberer.Revit.csproj"
$AddinsFolder  = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
$BuildOutput   = "$PSScriptRoot\..\src\Revit\bin\Debug"
$DllSource     = "$BuildOutput\EllahColNumPro.dll"
$CoreDllSource = "$BuildOutput\EllahColNumPro.Core.dll"
$AddinSource   = "$PSScriptRoot\..\src\Revit\EllahColNumPro.addin"
# ─────────────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "=== ELLAH-ColNum Pro — Deploy to Revit $RevitVersion ===" -ForegroundColor Cyan
Write-Host ""

# Step 1 — Check Revit is not running (can't overwrite a locked DLL)
$revitProcess = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Host "[!] Revit is currently running. Please close Revit first." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

# Step 2 — Build the project (full rebuild)
Write-Host "[1/3] Building project (clean build)..." -ForegroundColor Yellow
dotnet build $ProjectPath --configuration Debug 2>&1 | Tee-Object -Variable buildOutput | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[!] Build FAILED. Errors:" -ForegroundColor Red
    $buildOutput | Select-String "error" | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Read-Host "Press Enter to exit"
    exit 1
}
Write-Host "      Build succeeded." -ForegroundColor Green

# Step 3 — Create Addins folder if it doesn't exist
Write-Host "[2/3] Preparing Addins folder..." -ForegroundColor Yellow
if (-not (Test-Path $AddinsFolder)) {
    New-Item -ItemType Directory -Path $AddinsFolder -Force | Out-Null
    Write-Host "      Created: $AddinsFolder" -ForegroundColor Gray
} else {
    Write-Host "      Found:   $AddinsFolder" -ForegroundColor Gray
}

# Step 4 — Copy all required files
Write-Host "[3/3] Copying files to Revit $RevitVersion Addins..." -ForegroundColor Yellow

foreach ($src in @($DllSource, $CoreDllSource, $AddinSource)) {
    if (-not (Test-Path $src)) {
        Write-Host "[!] File not found: $src" -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
    Copy-Item -Path $src -Destination $AddinsFolder -Force
    Write-Host "      OK: $(Split-Path $src -Leaf)" -ForegroundColor Green
}

# Done
Write-Host ""
Write-Host "=== Deploy complete! ===" -ForegroundColor Cyan
Write-Host "Open Revit $RevitVersion — you will see the updated ELLAH-ColNum Pro tab." -ForegroundColor White
Write-Host ""
Read-Host "Press Enter to close"
