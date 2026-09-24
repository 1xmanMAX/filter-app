@echo off
dotnet publish src\FilterApp\FilterApp.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
