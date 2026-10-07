# Starts GrannyCraft: Minecraft (Prism, "GrannyCraft" instance) first, then the modded copy of Granny Legacy.
#   Play.bat            borderless fullscreen
#   Play.bat -Windowed  1600x900 window
param([switch]$Windowed)
. "$PSScriptRoot\common.ps1"

if (-not (Test-Installed)) {
    Write-Host "GrannyCraft is not set up yet (or a part is missing): run Setup.bat first." -ForegroundColor Yellow
    exit 1
}

# Granny Legacy quits at the menu when Steam is not running
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
    Write-Host "Starting Steam (Granny Legacy needs it)..."
    Start-Process "steam://open/main"
    for ($i = 0; $i -lt 60 -and -not (Get-Process steam -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    Start-Sleep 10
}

$minecraftUp = Get-NetTCPConnection -LocalPort 25600 -State Listen -ErrorAction SilentlyContinue
if ($minecraftUp) {
    Write-Host "Minecraft is already running."
} else {
    Update-JavaPaths
    $log = Join-Path $Instance "minecraft\logs\latest.log"
    $started = Get-Date
    Start-Process (Join-Path $Launcher "prismlauncher.exe") -ArgumentList "--launch", "GrannyCraft" -WorkingDirectory $Launcher
    Write-Host "Starting Minecraft... (the first time Prism asks you to sign in, then downloads Minecraft: a few minutes)"
    $ready = $false
    for ($i = 0; $i -lt 900 -and -not $ready; $i++) {
        Start-Sleep 1
        $ready = (Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started -and (Select-String $log -Pattern "joined the game" -Quiet)
    }
    if (-not $ready) { Write-Host "Minecraft did not reach the world in 15 minutes: starting Granny anyway." -ForegroundColor Yellow }
}

# the modded copy only (never your Steam install)
$exe = Join-Path $Game "Granny Legacy.exe"
Get-Process "Granny Legacy" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
if ($Windowed) { $grannyArgs = @("-screen-fullscreen", "0", "-screen-width", "1600", "-screen-height", "900") }
else { $grannyArgs = @("-screen-fullscreen", "1", "-window-mode", "borderless") }
Start-Process $exe -WorkingDirectory $Game -ArgumentList $grannyArgs
Write-Host ""
Write-Host "Granny Legacy is starting (the very first start with BepInEx takes 1-3 minutes, then press Play in Granny)." -ForegroundColor Green
Write-Host "F8 = Minecraft passthrough on/off, F5 = camera view, F10 = breakable house on/off."
