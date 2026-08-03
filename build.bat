@echo off
setlocal

set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo Building SR160PowerConfig...
%CSC% /nologo /target:winexe /platform:x86 /win32icon:app.ico /out:SR160PowerConfig.exe ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  Program.cs MainForm.cs UHFAPI.cs Localization.cs WindowsKeyboard.cs RawInputKeyboard.cs Updater.cs VersionInfo.cs

if not %ERRORLEVEL% == 0 goto :failed
echo   OK: SR160PowerConfig.exe

echo.
echo Building installer...
rem The app exe is embedded into Setup.exe, so it must be built first (above).
%CSC% /nologo /target:winexe /platform:x86 /win32icon:app.ico /out:SR160PowerConfig_Setup.exe ^
  /reference:System.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /resource:SR160PowerConfig.exe,SR160PowerConfig.exe ^
  /resource:UHFAPI.dll,UHFAPI.dll ^
  /resource:libusb-1.0.dll,libusb-1.0.dll ^
  /resource:Logo.png,Logo.png ^
  Setup.cs

if not %ERRORLEVEL% == 0 goto :failed
echo   OK: SR160PowerConfig_Setup.exe

echo.
echo BUILD SUCCESSFUL
echo   SR160PowerConfig.exe        - the application (needs the DLLs beside it)
echo   SR160PowerConfig_Setup.exe  - standalone installer, everything embedded
goto :done

:failed
echo.
echo BUILD FAILED

:done
echo.
pause
