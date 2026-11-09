@echo off
setlocal
pushd "%~dp0"
if exist "publish\SimplePmxVeldrid.exe" (
  start "" "publish\SimplePmxVeldrid.exe" %*
) else (
  ..\..\work\dotnet\dotnet.exe run --project SimplePmxVeldrid.csproj -- %*
)
popd
