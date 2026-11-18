@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework C# compiler was not found.
  exit /b 1
)
pushd "%~dp0"
if not exist "build" mkdir "build"
for /f "usebackq delims=" %%V in ("..\VERSION") do set "APP_VERSION=%%V"
if not defined APP_VERSION (
  echo VERSION file is missing or empty.
  popd
  exit /b 1
)
for /f "tokens=1-3 delims=." %%A in ("%APP_VERSION%") do set "APP_VERSION_3=%%A.%%B.%%C"
>"build\VersionInfo.cs" echo using System.Reflection;
>>"build\VersionInfo.cs" echo [assembly: AssemblyVersion("%APP_VERSION_3%.0")]
>>"build\VersionInfo.cs" echo [assembly: AssemblyFileVersion("%APP_VERSION_3%.0")]
"%CSC%" /nologo /optimize+ /target:winexe /out:"build\Bubblecast.exe" /win32icon:"Bubblecast.ico" /win32manifest:"app.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /reference:System.Security.dll /resource:"Bubblecast.ico",Bubblecast.AppIcon.ico /resource:"BrandMark.png",Bubblecast.BrandMark.png "build\VersionInfo.cs" "src\*.cs" "shared\*.cs"
if errorlevel 1 exit /b 1
"%CSC%" /nologo /optimize+ /target:winexe /out:"build\Bubblecast Setup.exe" /win32icon:"Bubblecast.ico" /win32manifest:"installer.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /resource:"build\Bubblecast.exe",BubblecastSetup.Payload.exe /resource:"Bubblecast.ico",Bubblecast.AppIcon.ico /resource:"BrandMark.png",Bubblecast.BrandMark.png "build\VersionInfo.cs" "BubblecastSetup.cs" "shared\*.cs"
if errorlevel 1 exit /b 1
echo Built:
echo   %CD%\build\Bubblecast.exe
echo   %CD%\build\Bubblecast Setup.exe
if /i "%~1"=="test" (
  "%CSC%" /nologo /target:exe /out:"build\RelayChecks.exe" /reference:"build\Bubblecast.exe" "tests\RelayChecks.cs"
  if errorlevel 1 exit /b 1
  "build\RelayChecks.exe"
  if errorlevel 1 exit /b 1
)
if /i "%~1"=="release" (
  if not exist "release" mkdir "release"
  copy /y "build\Bubblecast.exe" "release\Bubblecast.exe" >nul || exit /b 1
  copy /y "build\Bubblecast Setup.exe" "release\Bubblecast Setup.exe" >nul || exit /b 1
  for /f "tokens=*" %%C in ('git -C ".." rev-parse HEAD') do set "SOURCE_COMMIT=%%C"
  for /f "tokens=*" %%C in ('powershell -NoProfile -Command "[System.Diagnostics.FileVersionInfo]::GetVersionInfo('%CSC%').FileVersion"') do set "COMPILER_VERSION=%%C"
  set "COMPILER_PATH=%CSC%"
  for /f "tokens=*" %%D in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-ddTHH:mm:ssK"') do set "BUILD_DATE=%%D"
  powershell -NoProfile -Command "$ErrorActionPreference = 'Stop'; try { $files = @('Bubblecast.exe','Bubblecast Setup.exe'); $sha = [System.Security.Cryptography.SHA256]::Create(); $lines = foreach ($name in $files) { $stream = [System.IO.File]::OpenRead((Join-Path 'release' $name)); try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', ''); $hash + '  ' + $name } finally { $stream.Dispose() } }; Set-Content -LiteralPath 'release\SHA256SUMS' -Value $lines -Encoding ascii; $notes = @('# Release notes', '', ('Version: ' + $env:APP_VERSION), ('Source commit: ' + $env:SOURCE_COMMIT), ('Compiler path: ' + $env:COMPILER_PATH), ('Compiler version: ' + $env:COMPILER_VERSION), ('Build date: ' + $env:BUILD_DATE), '', 'Both Windows deliverables were built successfully from this source revision.'); Set-Content -LiteralPath 'release\RELEASE-NOTES.md' -Value $notes -Encoding utf8 } catch { Write-Error $_; exit 1 }"
  if errorlevel 1 exit /b 1
  echo Release deliverables copied to release\ with SHA256SUMS and RELEASE-NOTES.md.
)
popd
