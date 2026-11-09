@echo off
setlocal
pushd "%~dp0"
if exist "publish\SimplePmxVeldrid.exe" (
  start "" "publish\SimplePmxVeldrid.exe" %*
) else (
  dotnet run --project SimplePmxVeldrid.csproj -- %*
)
popd
