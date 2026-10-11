@echo off
rem Double-click to start real PostgreSQL and MariaDB test servers (127.0.0.1 only). See docs\testing-databases.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-TestServers.ps1" %*
set RC=%ERRORLEVEL%
echo.
if /i not "%GITHUB_ACTIONS%"=="true" pause
exit /b %RC%
