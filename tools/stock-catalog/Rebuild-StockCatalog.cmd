@echo off
rem Rebuilds stock-catalog.json from the sources named in extract-settings.json.
rem Double-click this file; no options are needed. On the first run it creates extract-settings.json
rem next to it: open that file, write the paths of your export and seed files into it, and run again.
cd /d "%~dp0"
where python >nul 2>nul
if errorlevel 1 (
  echo Python was not found on this computer. Install Python 3 from python.org, then run this file again.
  pause
  exit /b 1
)
python extract_stock.py
if errorlevel 1 pause
