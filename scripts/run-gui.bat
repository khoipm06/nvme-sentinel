@echo off
setlocal
pushd "%~dp0\.."
dotnet run -c Release --project src\NvmeSentinel.App\NvmeSentinel.App.csproj -- %*
popd
