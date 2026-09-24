@echo off
rem Builds the downloadable release: a self-contained FilterApp.exe (no .NET install needed), zipped with
rem the .cmd installer, and FilterApp-Setup.exe.
setlocal
set "OUT=release\FilterApp"
if exist release rmdir /s /q release
dotnet publish src\FilterApp\FilterApp.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true -p:DebugType=none -o "%OUT%" || exit /b 1
copy /y installer\Instalar.cmd "%OUT%" >nul
copy /y installer\Desinstalar.cmd "%OUT%" >nul
powershell -NoProfile -Command "Compress-Archive -Path 'release\FilterApp\*' -DestinationPath 'release\FilterApp-win-x64.zip' -Force"

rem Setup.exe (Inno Setup 6: scoop install extras/inno-setup, or the official installer).
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content src\FilterApp\FilterApp.csproj)).Project.PropertyGroup.Version"`) do set "VERSION=%%v"
set "ISCC=%USERPROFILE%\scoop\apps\inno-setup\current\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (echo Inno Setup not found: skipping FilterApp-Setup.exe & exit /b 0)
"%ISCC%" /Q /DMyVersion=%VERSION% installer\FilterApp.iss || exit /b 1
