@echo off
chcp 65001 > nul
setlocal enabledelayedexpansion

cd /d "%~dp0"
echo ==============================================================================
echo  COMIC READER - MASTER MULTI-PLATFORM BUILD (WINDOWS ^& ANDROID)
echo ==============================================================================

rem 1. Tat tien trinh cu neu dang chay
taskkill /im "ComicReader.exe" /f >nul 2>&1
taskkill /im "ComicReader.Desktop.exe" /f >nul 2>&1

set "PUBLISH_DIR=%~dp0publish"
set "ARTIFACTS_DIR=%~dp0artifacts"

if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"
if not exist "%ARTIFACTS_DIR%" mkdir "%ARTIFACTS_DIR%"

echo.
echo [1/4] Dang don dep thu muc build...
if exist "%ARTIFACTS_DIR%\win-x64" rd /s /q "%ARTIFACTS_DIR%\win-x64"
mkdir "%ARTIFACTS_DIR%\win-x64"

echo.
echo [2/4] Dang build Windows x64 Single-file Standalone EXE...
dotnet publish "%~dp0ComicReader.Desktop\ComicReader.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%ARTIFACTS_DIR%\win-x64"
if %ERRORLEVEL% NEQ 0 (
    echo [!] LOI: Build Windows Desktop that bai!
    pause
    exit /b %ERRORLEVEL%
)

if exist "%ARTIFACTS_DIR%\win-x64\ComicReader.Desktop.exe" (
    copy /y "%ARTIFACTS_DIR%\win-x64\ComicReader.Desktop.exe" "%PUBLISH_DIR%\ComicReader.exe" > nul
    echo   -^> Da tao Windows EXE: "%PUBLISH_DIR%\ComicReader.exe"
) else if exist "%ARTIFACTS_DIR%\win-x64\ComicReader.exe" (
    copy /y "%ARTIFACTS_DIR%\win-x64\ComicReader.exe" "%PUBLISH_DIR%\ComicReader.exe" > nul
    echo   -^> Da tao Windows EXE: "%PUBLISH_DIR%\ComicReader.exe"
) else (
    echo [!] Canh bao: Khong tim thay file EXE trong artifacts!
)

echo.
echo [3/4] Dang build Android Release Signed APK...
dotnet build "%~dp0ComicReader.Android\ComicReader.Android.csproj" -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [!] LOI: Build Android APK that bai!
    pause
    exit /b %ERRORLEVEL%
)

set "ANDROID_BIN=%~dp0ComicReader.Android\bin\Release\net10.0-android"
set "SIGNED_APK="

for %%F in ("%ANDROID_BIN%\*-Signed.apk") do (
    set "SIGNED_APK=%%~fF"
)

if defined SIGNED_APK (
    copy /y "!SIGNED_APK!" "%PUBLISH_DIR%\ComicReader-Signed.apk" > nul
    echo   -^> Da tao Android Signed APK: "%PUBLISH_DIR%\ComicReader-Signed.apk"
) else (
    for %%F in ("%ANDROID_BIN%\*.apk") do (
        copy /y "%%~fF" "%PUBLISH_DIR%\ComicReader.apk" > nul
        echo   -^> Da tao Android APK: "%PUBLISH_DIR%\ComicReader.apk"
    )
)

echo.
echo [4/4] Tong ket san pham xuat ban tai: %PUBLISH_DIR%
echo ------------------------------------------------------------------------------
dir "%PUBLISH_DIR%" | findstr /i "\.exe \.apk"
echo ------------------------------------------------------------------------------

echo.
echo [V] HOAN TAT MASTER BUILD (WINDOWS + ANDROID) THANH CONG 0 ERROR!
echo.
pause
