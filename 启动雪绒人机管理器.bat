@echo off
setlocal enabledelayedexpansion
title Xuerong Bot Manager
cd /d "%~dp0"

rem ---------- locate node ----------
set "NODE_EXE="
where node >nul 2>&1 && set "NODE_EXE=node"

if not defined NODE_EXE (
  for %%P in (
    "%ProgramFiles%\nodejs\node.exe"
    "%ProgramFiles(x86)%\nodejs\node.exe"
    "%USERPROFILE%\.workbuddy\binaries\node\versions\22.22.2-6\node.exe"
    "%LOCALAPPDATA%\fnm\node-versions\v22\installation\node.exe"
    "%LOCALAPPDATA%\Programs\nodejs\node.exe"
  ) do (
    if exist %%P (
      set "NODE_EXE=%%~fP"
      goto :found
    )
  )
)

:found
if not defined NODE_EXE (
  echo [ERROR] Node.js was not found.
  echo Please install Node.js 18+ from https://nodejs.org/
  pause
  exit /b 1
)

echo Xuerong Bot Manager v1.0.0
echo Starting server on http://localhost:3721 ...
start "" "http://localhost:3721"

"%NODE_EXE%" server.js

echo.
echo [ERROR] Server exited with code %errorlevel%.
echo Port 3721 may already be in use.
pause
