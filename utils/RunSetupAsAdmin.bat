@echo off
:: Check for administrative permissions
net session >nul 2>&1
if %errorLevel% == 0 (
    echo Running with Admin privileges...
) else (
    echo Requesting Admin privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

:: Run the PowerShell script
powershell.exe -ExecutionPolicy Bypass -File "%~dp0SetupOpenness.ps1"

:: Check if the current user session has the "Siemens TIA Openness" group token
whoami /groups | find /I "Siemens TIA Openness" >nul
if %errorLevel% == 0 (
    echo [OK] Your current session has the required group membership.
    set "group_ok=true"
) else (
    echo [WARNING] Your current session DOES NOT have the "Siemens TIA Openness" group token!
    echo.
    echo *******************************************************************************
    echo * CRITICAL: A RESTART OR LOGOFF IS REQUIRED!                              *
    echo * You have been added to the group, but Windows has not updated your token.   *
    echo * Any attempt to run the application now WILL FAIL with a Security Exception. *
    echo * Please LOG OFF and LOG IN again (or restart) before running the app.        *
    echo *******************************************************************************
    set "group_ok=false"
)

echo.
echo ===============================================================================
echo SETUP COMPLETE
echo ===============================================================================
echo.

if "%group_ok%"=="true" (
    set /p run_app="Do you want to run TIA Openness Studio now? (Y/N): "
    if /i "%run_app%"=="Y" (
        if exist "%~dp0..\TIAOpennessStudio\bin\Debug\net48\TIAOpennessStudio.exe" (
            start "" "%~dp0..\TIAOpennessStudio\bin\Debug\net48\TIAOpennessStudio.exe"
        ) else if exist "%~dp0..\TIAOpennessStudio\bin\Debug\TIAOpennessStudio.exe" (
            start "" "%~dp0..\TIAOpennessStudio\bin\Debug\TIAOpennessStudio.exe"
        ) else (
            echo.
            echo Could not find TIAOpennessStudio.exe in bin\Debug or bin\Debug\net48.
            echo Please ensure the project is built.
            pause
        )
    )
) else (
    echo Skipping application launch due to missing group token.
    echo Please restart your computer to apply changes.
    pause
)

