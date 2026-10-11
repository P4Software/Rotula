@echo off
rem Double-click to build the optional database add-ons into artifacts\packs. Everything is in build-packs.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-packs.ps1"
exit /b %errorlevel%
