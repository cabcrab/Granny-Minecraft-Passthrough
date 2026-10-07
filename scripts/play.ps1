# Starts Granny Minecraft Passthrough: Minecraft first, then the modded copy of Granny Legacy.
#   Play.bat            borderless fullscreen
#   Play.bat -Windowed  1600x900 window
param([switch]$Windowed)
. "$PSScriptRoot\common.ps1"

$state = Read-State
if (-not (Test-Installed $state)) {
    Write-Host "Not set up yet (or a part is missing): run Setup.bat first." -ForegroundColor Yellow
    exit 1
}

# Granny Legacy quits at the menu when Steam is not running
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) {
    Write-Host "Starting Steam (Granny Legacy needs it)..."
    Start-Process "steam://open/main"
    for ($i = 0; $i -lt 60 -and -not (Get-Process steam -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    Start-Sleep 10
}

# the mod opens its link (127.0.0.1:25600) as soon as Minecraft starts
function Test-Minecraft { [bool](Get-NetTCPConnection -LocalPort 25600 -State Listen -ErrorAction SilentlyContinue) }

if (Test-Minecraft) {
    Write-Host "Minecraft is already running."
} elseif ($state.mode -eq "prism") {
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
} else {
    # the folder may have moved since setup: the version must point at this folder's mods
    Write-MinecraftVersion $state.minecraftDir
    Write-Host ""
    Write-Host "Now start Minecraft from your launcher with the version:" -ForegroundColor Green
    Write-Host "    $VersionId" -ForegroundColor White
    Write-Host '(official Minecraft Launcher: the installation "Granny Minecraft Passthrough"; other launchers: pick it in'
    Write-Host " the version list). Granny Legacy starts by itself once Minecraft is running."
    Write-Host "Waiting for Minecraft..."
    for ($i = 0; $i -lt 3600 -and -not (Test-Minecraft); $i++) { Start-Sleep 1 }
    if (-not (Test-Minecraft)) { Write-Host "Minecraft did not start within an hour." -ForegroundColor Yellow; exit 1 }
    Write-Host "Minecraft is running: it opens its own world, Granny starts in a moment."
    Start-Sleep 15
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
