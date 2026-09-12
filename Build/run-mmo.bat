@echo off
setlocal
title MMORPG Kit CE - MMO launcher (server-meshing spikes)

REM ============================================================================
REM  MMO server launcher for the server-meshing spikes (Spike 2 full rig).
REM
REM  YOU DO NOT NEED THIS FOR THE MINIMAL RIG. The recommended first Spike 2
REM  measurement (self-migration, one map) runs the SERVER IN THE UNITY EDITOR:
REM  open 00Init_MMO, enable start*OnAwake on MMOServerInstance, press Play, and
REM  run the client as a ParrelSync clone. See Docs\ServerMeshing\SPIKE2.md (Option 0).
REM
REM  This batch is for the BUILD-BASED rig you need for a real A->B hop:
REM  it launches your SERVER build with the kit's command-line args.
REM ============================================================================

REM ----------------------------- EDIT THESE -----------------------------------
REM Folder containing your SERVER build. MapSpawn launches map servers from
REM ".\Build.exe" relative to THIS folder, so the exe should be named Build.exe
REM (or change `spawnExePath` on MapSpawnNetworkManager to match).
set "SERVER_DIR=D:\Game Development\mmo-server-meshing\Build\Server"
set "SERVER_EXE=%SERVER_DIR%\kit-ce-project.exe"

REM Optional: a CLIENT build exe (leave blank to disable option 3).
set "CLIENT_EXE="

REM For launching an EXTRA standalone map server (advanced; A->B Option B).
REM Match these to a real map id / channel id from your game data + channel config.
set "MAP_NAME=Map001"
set "CHANNEL_ID=default"
set "MAP_PORT=6010"
set "CENTRAL_ADDRESS=127.0.0.1"
REM ----------------------------------------------------------------------------

REM Working dir must be the build folder so MapSpawn finds .\Build.exe.
cd /d "%SERVER_DIR%" 2>nul

if not exist "%SERVER_EXE%" (
  echo.
  echo [!] Server build not found at:
  echo     %SERVER_EXE%
  echo.
  echo  1^) Build a Windows SERVER/standalone build via the kit's Build menu.
  echo  2^) Name the output Build.exe ^(or set spawnExePath on MapSpawnNetworkManager^).
  echo  3^) Set SERVER_DIR / SERVER_EXE at the top of this file.
  echo.
  echo  Reminder: the MINIMAL Spike 2 rig needs NO build - run the server in the
  echo  Unity editor instead. See Docs\ServerMeshing\SPIKE2.md ^(Option 0^).
  echo.
  pause
  exit /b 1
)

:menu
echo.
echo ============================ MMO launcher ============================
echo   1^) Start full server stack   (Database + Central + Login + MapSpawn)
echo   2^) Start standalone MAP server  (%MAP_NAME% / ch:%CHANNEL_ID% / port:%MAP_PORT%)
echo   3^) Start client build
echo   Q^) Quit
echo =====================================================================
echo  Tip: usually you only need [1] - MapSpawn auto-spawns map servers as
echo       players enter maps. Use [2] only for manual A-B control.
echo.
set /p "sel=Select: "

if /i "%sel%"=="1" goto stack
if /i "%sel%"=="2" goto mapserver
if /i "%sel%"=="3" goto client
if /i "%sel%"=="Q" exit /b 0
goto menu

:stack
echo Starting full server stack...
start "MMO Server Stack" "%SERVER_EXE%" -startDatabaseServer -startCentralServer -startLoginServer -startMapSpawnServer
echo Started. MapSpawn will launch map servers on demand from .\Build.exe in this folder.
goto menu

:mapserver
echo Starting standalone map server: %MAP_NAME% (channel %CHANNEL_ID%, port %MAP_PORT%)...
start "Map %MAP_NAME% ch:%CHANNEL_ID%" "%SERVER_EXE%" -startMapServer -mapName %MAP_NAME% -channelId %CHANNEL_ID% -mapPort %MAP_PORT% -centralAddress %CENTRAL_ADDRESS%
echo  (If it fails to register, also pass -clusterPort / -centralPort to match your config.)
goto menu

:client
if "%CLIENT_EXE%"=="" (
  echo [!] Set CLIENT_EXE at the top of this file first ^(or just run the client from the editor / a ParrelSync clone^).
  goto menu
)
echo Starting client...
start "MMO Client" "%CLIENT_EXE%"
goto menu
