@echo off
rem Double-click to rewrite docs\zpl-coverage.md from the ZPL command catalogue.
cd /d "%~dp0..\.."
dotnet run --project tools\gen-zpl-coverage
echo.
pause
