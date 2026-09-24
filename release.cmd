@echo off
rem Builds the downloadable release: a self-contained FilterApp.exe (no .NET install needed) plus the installer, zipped.
setlocal
set "OUT=release\FilterApp"
if exist release rmdir /s /q release
dotnet publish src\FilterApp\FilterApp.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true -p:DebugType=none -o "%OUT%" || exit /b 1
copy /y installer\Instalar.cmd "%OUT%" >nul
copy /y installer\Desinstalar.cmd "%OUT%" >nul
powershell -NoProfile -Command "Compress-Archive -Path 'release\FilterApp\*' -DestinationPath 'release\FilterApp-win-x64.zip' -Force"
