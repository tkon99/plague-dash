@echo off
REM Plague Dash — one-click launcher for the local dashboard server.
REM Tries `python`, then `py`, then gives up with a helpful message.
setlocal
cd /d "%~dp0dashboard"

where python >nul 2>nul
if %errorlevel%==0 (
    python serve.py
    goto :end
)

where py >nul 2>nul
if %errorlevel%==0 (
    py serve.py
    goto :end
)

echo.
echo [Plague Dash] Python was not found on PATH.
echo Install Python from https://python.org (and tick "Add to PATH"),
echo or run the dashboard with any static file server on port 8765.
echo.

:end
endlocal
