@echo off
rem Installs GrannyCraft inside this folder (copy of Granny Legacy + BepInEx + plugin, portable Prism Launcher + Minecraft).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup.ps1" %*
echo.
pause
