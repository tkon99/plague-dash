@echo off
REM PlagueDash mod build + deploy script.
REM
REM Builds PlagueDash.dll with MSBuild, then copies the compiled DLL (taken from
REM obj\Release, MSBuild's always-correct output) + Info.json into the game's
REM Mods folder so the next game launch picks it up.
REM
REM Usage:
REM   build.bat            -> build Release + deploy to game
REM   build.bat /nodeploy  -> build only, do not copy to game
REM
REM Requires:
REM   - Visual Studio 2022 BuildTools (or full VS 2022) for MSBuild
REM   - UnityModManager installed for Plague Inc
REM   - If the .NET 4.8 Developer Pack is absent, the project falls back to the
REM     offline reference assemblies under tools\net48ref (already included).
REM
setlocal enabledelayedexpansion

set "DEPLOY=1"
if /i "%~1"=="/nodeploy" set "DEPLOY=0"

set "HERE=%~dp0"

REM --- locate MSBuild (VS 2022 BuildTools or full VS) ---
set "MSBUILD="
for %%Y in (BuildTools Enterprise Professional Community) do (
  if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\%%Y\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\%%Y\MSBuild\Current\Bin\MSBuild.exe"
)
if not defined MSBUILD (
  for %%Y in (BuildTools Enterprise Professional Community) do (
    if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\%%Y\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\%%Y\MSBuild\Current\Bin\MSBuild.exe"
  )
)
if not defined MSBUILD (
  echo [build] MSBuild not found. Install Visual Studio 2022 Build Tools.
  exit /b 1
)
echo [build] MSBuild: !MSBUILD!

REM --- build (use -p: not /p: ; /p: breaks under Git Bash) ---
"!MSBUILD!" "%HERE%PlagueDash.csproj" -p:Configuration=Release -p:CopyToMods=false -nologo -v:minimal
if errorlevel 1 (
  echo [build] BUILD FAILED.
  exit /b 1
)

REM The compiler writes to obj\Release; MSBuild copies that to bin\Release. If the
REM game is running it can lock bin\Release\PlagueDash.dll and truncate the copy,
REM so always read the canonical artifact from obj\Release.
set "DLLSRC=%HERE%obj\Release\PlagueDash.dll"
if not exist "%DLLSRC%" set "DLLSRC=%HERE%bin\Release\PlagueDash.dll"
for %%F in ("%DLLSRC%") do set "DLLSIZE=%%~zF"
echo [build] OK - source DLL: %DLLSRC% ^(!DLLSIZE! bytes^)

if "%DEPLOY%"=="0" (
  echo [build] Skipped deploy ^(/nodeploy^).
  exit /b 0
)

REM --- deploy to game (resolve PF(x86) without tripping the batch parser) ---
set "PF86=%ProgramFiles(x86)%"
if not defined PF86 set "PF86=%ProgramFiles%"
set "MODSDIR=%PF86%\Steam\steamapps\common\PlagueInc\Mods\PlagueDash"
if not exist "%MODSDIR%" mkdir "%MODSDIR%"

REM Copy to a temp name first, then move (atomic-ish: avoids leaving a truncated
REM DLL if the game is holding the target open).
copy /Y "%DLLSRC%" "%MODSDIR%\PlagueDash.new" >nul 2>&1
move /Y "%MODSDIR%\PlagueDash.new" "%MODSDIR%\PlagueDash.dll" >nul 2>&1
copy /Y "%HERE%Info.json" "%MODSDIR%\" >nul 2>&1
echo [build] Deployed to "%MODSDIR%"

endlocal
