@echo off
setlocal
pushd "%~dp0"
..\..\work\dotnet\dotnet.exe publish SimplePmxVeldrid.csproj -c Release -o publish
popd
