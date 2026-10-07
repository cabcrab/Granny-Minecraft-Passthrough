# Removes what Setup.bat added OUTSIDE this folder (only in "your own launcher" mode): the version
# versions\Granny-Minecraft-Passthrough and the "Granny Minecraft Passthrough" installation in launcher_profiles.json.
# Everything else is inside this folder: delete the folder afterwards.
. "$PSScriptRoot\common.ps1"

$state = Read-State
if ($state -and $state.minecraftDir) {
    $dir = Join-Path $state.minecraftDir "versions\$VersionId"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Host "removed $dir" }
    Remove-LauncherProfile $state.minecraftDir
    Write-Host 'removed the installation "Granny Minecraft Passthrough" (if it was there)'
}
Write-Host ""
Write-Host "Nothing of Granny Minecraft Passthrough is left outside this folder. You can now delete the folder:" -ForegroundColor Green
Write-Host "    $Root"
