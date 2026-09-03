@echo off
title BINDER Climate Chamber Control v3.4
cd /d "%~dp0"

where python >nul 2>nul
if %errorlevel% neq 0 (
    echo Python was not found.
    echo Install Python 3 and run this file again.
    pause
    exit /b 1
)

python binder_control_v3_4_COMMENTED.py

if %errorlevel% neq 0 (
    echo.
    echo Program stopped with an error.
    pause
)
