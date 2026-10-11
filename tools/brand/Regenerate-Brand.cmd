@echo off
rem Double-click to redraw the Rotula logo and icons and rewrite every brand file in the repository.
rem Nothing to configure: the drawing lives in the code of this folder.
cd /d "%~dp0..\.."
dotnet run --project tools\brand -c Release
echo.
pause
