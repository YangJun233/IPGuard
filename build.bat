@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" echo [ERROR] csc.exe not found - install .NET Framework 4.x& exit /b 1
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%~dp0IPGuard.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /win32icon:"%~dp0app.ico" "%~dp0src\IpRegionGuard.cs"
if errorlevel 1 echo [ERROR] build failed& exit /b 1
echo [OK] built IPGuard.exe
endlocal
