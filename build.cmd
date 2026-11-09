@echo off
setlocal
pushd "%~dp0"
dotnet publish SimplePmxVeldrid.csproj -c Release -o publish
popd
