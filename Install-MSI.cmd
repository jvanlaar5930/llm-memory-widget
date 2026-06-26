@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MSI.ps1" %*

if errorlevel 1 (
    echo.
    echo Install failed.
    exit /b %errorlevel%
)
