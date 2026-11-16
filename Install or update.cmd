@echo off
setlocal
title Decklore setup
set "SRC=%~dp0"
set "DEST=%LOCALAPPDATA%\Programs\Decklore"
set "OLD=%LOCALAPPDATA%\Programs\TCG deck scanner"

echo.
echo  Decklore setup
echo  --------------
echo  This builds the app, installs it to:
echo    %DEST%
echo  and adds shortcuts to your Desktop and Start menu.
echo.

taskkill /im "Decklore.exe" /f >nul 2>&1
taskkill /im "TCG deck scanner.exe" /f >nul 2>&1

echo  Building the app (this takes about a minute)...
dotnet publish "%SRC%Windows\TcgScanner.csproj" -c Release -o "%DEST%" --nologo -v q
if errorlevel 1 (
    echo.
    echo  The build failed. Make sure the .NET 10 SDK is installed, then try again.
    pause
    exit /b 1
)

rem The app was called TCG deck scanner before version 1.3. Remove the old program and its shortcuts.
rem Your collection is not stored there; Decklore copies it over the first time it opens.
if exist "%OLD%\TCG deck scanner.exe" rmdir /s /q "%OLD%"
powershell -NoProfile -Command "foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { Remove-Item -LiteralPath (Join-Path $dir 'TCG deck scanner.lnk') -ErrorAction SilentlyContinue }"

echo  Creating shortcuts...
powershell -NoProfile -Command "$s = New-Object -ComObject WScript.Shell; $exe = Join-Path $env:LOCALAPPDATA 'Programs\Decklore\Decklore.exe'; foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { $l = $s.CreateShortcut((Join-Path $dir 'Decklore.lnk')); $l.TargetPath = $exe; $l.WorkingDirectory = Split-Path $exe; $l.IconLocation = $exe + ',0';$l.Description = 'Scan trading cards, track value, and build decks'; $l.Save() }"

echo.
echo  Done. Open Decklore from your Desktop or the Start menu.
echo  Your collection and settings are kept when you update.
echo.
start "" "%DEST%\Decklore.exe"
ping -n 6 127.0.0.1 >nul
