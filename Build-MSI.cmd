@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-MSI.ps1" %*

if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b %errorlevel%
)

echo.
echo Build completed.
