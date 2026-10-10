@echo off
rem Double-click to build the signed Rotula installer. Everything is in build-installer.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1"
exit /b %errorlevel%
