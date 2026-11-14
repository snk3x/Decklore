@echo off
setlocal
title TCG deck scanner setup
set "SRC=%~dp0"
set "DEST=%LOCALAPPDATA%\Programs\TCG deck scanner"

echo.
echo  TCG deck scanner setup
echo  ----------------------
echo  This builds the app, installs it to:
echo    %DEST%
echo  and adds shortcuts to your Desktop and Start menu.
echo.

taskkill /im "TCG deck scanner.exe" /f >nul 2>&1

echo  Building the app (this takes about a minute)...
dotnet publish "%SRC%Windows\TcgScanner.csproj" -c Release -o "%DEST%" --nologo -v q
if errorlevel 1 (
    echo.
    echo  The build failed. Make sure the .NET 10 SDK is installed, then try again.
    pause
    exit /b 1
)

echo  Creating shortcuts...
powershell -NoProfile -Command "$s = New-Object -ComObject WScript.Shell; $exe = Join-Path $env:LOCALAPPDATA 'Programs\TCG deck scanner\TCG deck scanner.exe'; foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) { $l = $s.CreateShortcut((Join-Path $dir 'TCG deck scanner.lnk')); $l.TargetPath = $exe; $l.WorkingDirectory = Split-Path $exe; $l.IconLocation = $exe + ',0';$l.Description = 'Scan trading cards, track value, and build decks'; $l.Save() }"

echo.
echo  Done. Open TCG deck scanner from your Desktop or the Start menu.
echo  Your collection and settings are kept when you update.
echo.
start "" "%DEST%\TCG deck scanner.exe"
ping -n 6 127.0.0.1 >nul
