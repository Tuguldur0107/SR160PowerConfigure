@echo off
echo Building SR160PowerConfig (cross-platform)...
echo.

dotnet build -c Release

if %ERRORLEVEL% == 0 (
    echo.
    echo BUILD SUCCESSFUL
    echo Output: bin\Release\net8.0\SR160PowerConfig.exe
) else (
    echo.
    echo BUILD FAILED
)

pause
