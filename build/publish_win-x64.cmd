cd /d %~dp0

@REM This script is used to publish the project to a folder and compress it to a 7z file.
@REM You should have 7z installed and added to PATH.
@REM You should prepare ffmpeg.exe and ffplay.exe in the build directory.

set PUBLISH_DIR=..\src\TiktokLiveRec.WPF\bin\x64\Release\net9.0-windows10.0.26100.0\publish\win-x64
set APP_EXE=%PUBLISH_DIR%\TiktokLiveRec.exe

where 7z >nul 2>nul
if errorlevel 1 (
    echo [ERROR] 7z was not found in PATH.
    exit /b 1
)

where makemica >nul 2>nul
if errorlevel 1 (
    echo [ERROR] makemica was not found in PATH.
    exit /b 1
)

if not exist ffmpeg.exe (
    echo [ERROR] Missing build\ffmpeg.exe . Place ffmpeg.exe next to this script before publishing.
    exit /b 1
)

if not exist ffplay.exe (
    echo [ERROR] Missing build\ffplay.exe . Place ffplay.exe next to this script before publishing.
    exit /b 1
)

dotnet publish ..\src\TiktokLiveRec.WPF\TiktokLiveRec.WPF.csproj -c Release -p:PublishProfile=FolderProfile
if errorlevel 1 exit /b 1

if exist "%PUBLISH_DIR%\downloads" rd /s /q "%PUBLISH_DIR%\downloads"

copy /y ffmpeg.exe "%PUBLISH_DIR%\"
if errorlevel 1 exit /b 1

copy /y ffplay.exe "%PUBLISH_DIR%\"
if errorlevel 1 exit /b 1

if not exist "%APP_EXE%" (
    echo [ERROR] Missing published executable: %APP_EXE%
    exit /b 1
)

if not exist "%PUBLISH_DIR%\ffmpeg.exe" (
    echo [ERROR] Missing published ffmpeg.exe
    exit /b 1
)

if not exist "%PUBLISH_DIR%\ffplay.exe" (
    echo [ERROR] Missing published ffplay.exe
    exit /b 1
)

if not exist "%PUBLISH_DIR%\Assets\Danmu\sign.js" (
    echo [ERROR] Missing published Assets\Danmu\sign.js
    exit /b 1
)

if not exist "%PUBLISH_DIR%\Assets\Danmu\a_bogus.js" (
    echo [ERROR] Missing published Assets\Danmu\a_bogus.js
    exit /b 1
)

del /s /q publish.7z
7z a publish.7z "%PUBLISH_DIR%\*" -t7z -mx=5 -mf=BCJ2 -r -y
if errorlevel 1 exit /b 1

for /f "usebackq delims=" %%i in (`powershell -NoLogo -NoProfile -Command "Get-Content '..\src\TiktokLiveRec.WPF\TiktokLiveRec.WPF.csproj' | Select-String -Pattern '<AssemblyVersion>(.*?)</AssemblyVersion>' | ForEach-Object { $_.Matches.Groups[1].Value }"`) do @set version=%%i
del /s /q TiktokLiveRec_v%version%_win-x64.7z
makemica micasetup.json
if errorlevel 1 exit /b 1

rename publish.7z TiktokLiveRec_v%version%_win-x64.7z

@pause
