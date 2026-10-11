@echo off
rem Rebuilds src\Rotula.Core\Gs1\Data\gs1-dictionary.json from the GS1 Barcode Syntax Dictionary text file.
rem Double-click this file; no options are needed. On the first run it creates settings.json next to it:
rem open that file, write the path of the downloaded gs1-syntax-dictionary.txt into it, and run again.
cd /d "%~dp0"
where python >nul 2>nul
if errorlevel 1 (
  echo Python was not found on this computer. Install Python 3 from python.org, then run this file again.
  pause
  exit /b 1
)
python build_gs1_dictionary.py
if errorlevel 1 pause
