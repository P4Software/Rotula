@echo off
rem Double-click to stop the test servers and delete their data. Add -PurgeCache to also delete the downloaded archives.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stop-TestServers.ps1" %*
set RC=%ERRORLEVEL%
echo.
if /i not "%GITHUB_ACTIONS%"=="true" pause
exit /b %RC%
