@echo off
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set HERE=%~dp0
"%CSC%" /nologo /target:exe /optimize /r:System.Drawing.dll /out:"%HERE%MakeIcon.exe" "%HERE%MakeIcon.cs"
if errorlevel 1 exit /b 1
"%HERE%MakeIcon.exe" "%HERE%..\assets\DBA CNC Logo.png" "%HERE%app.ico"
if errorlevel 1 exit /b 1
"%CSC%" /nologo /target:winexe /optimize /win32manifest:"%HERE%app.manifest" /win32icon:"%HERE%app.ico" /resource:"%HERE%..\assets\logo.png",logo.png /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /out:"%HERE%DBA CNC.exe" "%HERE%JogTest.cs"
if errorlevel 1 exit /b 1
echo Built "%HERE%DBA CNC.exe"
