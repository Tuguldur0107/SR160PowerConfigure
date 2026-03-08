@echo off
echo Building SR160PowerConfig...

set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

%CSC% /target:winexe /platform:x86 /out:SR160PowerConfig.exe ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  Program.cs MainForm.cs UHFAPI.cs

if %ERRORLEVEL% == 0 (
    echo.
    echo BUILD SUCCESSFUL: SR160PowerConfig.exe
    echo.
    echo UHFAPI.dll болон libusb-1.0.dll файлуудыг SR160PowerConfig.exe-тэй нэг хавтаст хуулна уу.
) else (
    echo.
    echo BUILD FAILED
)

pause
