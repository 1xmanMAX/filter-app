@echo off
setlocal
set "DEST=%LOCALAPPDATA%\Programs\Filter App"

if not exist "%~dp0FilterApp.exe" (
  echo No se encontro FilterApp.exe junto a este archivo. Descomprime el ZIP completo primero.
  pause & exit /b 1
)
tasklist /fi "imagename eq FilterApp.exe" | find /i "FilterApp.exe" >nul && (
  echo Filter App esta abierta. Cierrala y vuelve a ejecutar este instalador.
  pause & exit /b 1
)

echo Instalando Filter App...
if not exist "%DEST%" mkdir "%DEST%"
copy /y "%~dp0FilterApp.exe" "%DEST%\FilterApp.exe" >nul || (echo No se pudo copiar la app. & pause & exit /b 1)
copy /y "%~dp0Desinstalar.cmd" "%DEST%\Desinstalar.cmd" >nul

rem Accesos directos (menu Inicio = aparece al buscar "Filter App") y registro en Configuracion > Aplicaciones.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dest = Join-Path $env:LOCALAPPDATA 'Programs\Filter App'; $exe = Join-Path $dest 'FilterApp.exe';" ^
  "$sh = New-Object -ComObject WScript.Shell;" ^
  "foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {" ^
  "  $l = $sh.CreateShortcut((Join-Path $dir 'Filter App.lnk')); $l.TargetPath = $exe; $l.WorkingDirectory = $dest;" ^
  "  $l.IconLocation = $exe + ',0'; $l.Description = 'Renombra y copia archivos a una carpeta'; $l.Save() }" ^
  "$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FilterApp'; New-Item $key -Force | Out-Null;" ^
  "$ver = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*','';" ^
  "@{ DisplayName = 'Filter App'; DisplayVersion = $ver; Publisher = '1xmanMAX'; DisplayIcon = $exe;" ^
  "   InstallLocation = $dest; UninstallString = [char]34 + (Join-Path $dest 'Desinstalar.cmd') + [char]34 }.GetEnumerator() |" ^
  "  ForEach-Object { Set-ItemProperty $key $_.Key $_.Value };" ^
  "Set-ItemProperty $key NoModify 1 -Type DWord; Set-ItemProperty $key NoRepair 1 -Type DWord"
if errorlevel 1 (echo No se pudieron crear los accesos directos. & pause & exit /b 1)

echo.
echo Listo. Busca "Filter App" en el menu Inicio o usa el icono del escritorio.
start "" "%DEST%\FilterApp.exe"
timeout /t 4 >nul
