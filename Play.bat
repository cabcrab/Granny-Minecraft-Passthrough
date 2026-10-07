@echo off
rem Starts Minecraft (GrannyCraft instance) and then the modded copy of Granny Legacy. Add -Windowed for a window.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\play.ps1" %*
if errorlevel 1 pause
