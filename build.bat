@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

rem ==============================================================================
rem  COMIC READER - MASTER MULTI-PLATFORM BUILD (WINDOWS & ANDROID)
rem  QUY TAC BAT BUOC: PUBLISH_DIR LUON LUON NAM O CUNG CAP FOLDER VOI workflow.md
rem ==============================================================================

cd /d "%~dp0"
echo ==============================================================================
echo  COMIC READER - MASTER MULTI-PLATFORM BUILD (WINDOWS ^& ANDROID)
echo  Output target: "%~dp0publish" (Cung cap folder voi workflow.md)
echo ==============================================================================

rem 1. Tat tien trinh cu neu dang chay
taskkill /im "ComicReader.exe" /f >nul 2>&1
taskkill /im "ComicReader.Desktop.exe" /f >nul 2>&1

set "SCRIPT_ROOT=%~dp0"
set "PUBLISH_DIR=%SCRIPT_ROOT%publish"
set "ARTIFACTS_DIR=%SCRIPT_ROOT%artifacts"

if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"
if not exist "%ARTIFACTS_DIR%" mkdir "%ARTIFACTS_DIR%"

echo.
echo [1/4] Dang don dep thu muc build...
if exist "%ARTIFACTS_DIR%\win-x64" rd /s /q "%ARTIFACTS_DIR%\win-x64"
mkdir "%ARTIFACTS_DIR%\win-x64"

where cargo >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [1b] Dang build Rust Native Engine (comic_engine.dll)...
    cargo build --manifest-path "%SCRIPT_ROOT%native\comic_engine\Cargo.toml" --release
    if exist "%SCRIPT_ROOT%native\comic_engine\target\release\comic_engine.dll" (
        copy /y "%SCRIPT_ROOT%native\comic_engine\target\release\comic_engine.dll" "%PUBLISH_DIR%\comic_engine.dll" > nul
    )
)

echo.
echo [2/4] Dang build Windows x64 Single-file Standalone EXE...
dotnet publish "%SCRIPT_ROOT%ComicReader.Desktop\ComicReader.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%ARTIFACTS_DIR%\win-x64"
if %ERRORLEVEL% NEQ 0 (
    echo [!] LOI: Build Windows Desktop that bai!
    pause
    exit /b %ERRORLEVEL%
)

if exist "%ARTIFACTS_DIR%\win-x64\ComicReader.Desktop.exe" (
    copy /y "%ARTIFACTS_DIR%\win-x64\ComicReader.Desktop.exe" "%PUBLISH_DIR%\ComicReader.exe" > nul
    echo   -^> Da tao Windows EXE tai: "%PUBLISH_DIR%\ComicReader.exe"
) else if exist "%ARTIFACTS_DIR%\win-x64\ComicReader.exe" (
    copy /y "%ARTIFACTS_DIR%\win-x64\ComicReader.exe" "%PUBLISH_DIR%\ComicReader.exe" > nul
    echo   -^> Da tao Windows EXE tai: "%PUBLISH_DIR%\ComicReader.exe"
) else (
    echo [!] Canh bao: Khong tim thay file EXE trong artifacts!
)

echo.
echo [3/4] Dang build Android Release Signed APK...
dotnet build "%SCRIPT_ROOT%ComicReader.Android\ComicReader.Android.csproj" -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [!] LOI: Build Android APK that bai!
    pause
    exit /b %ERRORLEVEL%
)

set "ANDROID_BIN=%SCRIPT_ROOT%ComicReader.Android\bin\Release\net10.0-android"
set "SIGNED_APK="

for %%F in ("%ANDROID_BIN%\*-Signed.apk") do (
    set "SIGNED_APK=%%~fF"
)

if defined SIGNED_APK (
    copy /y "!SIGNED_APK!" "%PUBLISH_DIR%\ComicReader-Signed.apk" > nul
    echo   -^> Da tao Android Signed APK tai: "%PUBLISH_DIR%\ComicReader-Signed.apk"
) else (
    for %%F in ("%ANDROID_BIN%\*.apk") do (
        copy /y "%%~fF" "%PUBLISH_DIR%\ComicReader.apk" > nul
        echo   -^> Da tao Android APK tai: "%PUBLISH_DIR%\ComicReader.apk"
    )
)

echo.
echo [4/4] Tong ket san pham xuat ban tai folder publish (cung cap voi workflow.md):
echo ------------------------------------------------------------------------------
dir "%PUBLISH_DIR%" | findstr /i "\.exe \.apk"
echo ------------------------------------------------------------------------------

echo.
echo [V] HOAN TAT MASTER BUILD (WINDOWS + ANDROID) THANH CONG 0 ERROR!
echo     Vi tri publish: "%PUBLISH_DIR%"
echo.
pause
