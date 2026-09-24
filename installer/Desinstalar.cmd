@echo off
setlocal
set "DEST=%LOCALAPPDATA%\Programs\Filter App"

tasklist /fi "imagename eq FilterApp.exe" | find /i "FilterApp.exe" >nul && (
  echo Filter App esta abierta. Cierrala y vuelve a intentarlo.
  pause & exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {" ^
  "  Remove-Item (Join-Path $dir 'Filter App.lnk') -ErrorAction SilentlyContinue }" ^
  "Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FilterApp' -Recurse -ErrorAction SilentlyContinue"

echo Filter App se desinstalo. Tus tarjetas guardadas quedan en %APPDATA%\FilterApp por si la vuelves a instalar.
timeout /t 4 >nul
rem This script lives inside the folder it deletes: leave the script first, then remove the folder.
cd /d "%TEMP%"
(goto) 2>nul & rmdir /s /q "%DEST%"
