@echo off
rem S8C.2 D12 test double for the docker CLI (LIVE-SCAN-01 self-tests, rows ST-08/ST-09). It never contacts any container or engine:
rem   * every invocation's arguments are appended to the file named by LEAKSCAN_SHIM_LOG (the redirect comes first so a trailing digit is never read as a handle);
rem   * `inspect` prints the scripted name from LEAKSCAN_SHIM_INSPECT_NAME (or fails when it is not set);
rem   * anything else (exec, psql, ...) fails with exit code 1 after being recorded, so a guard row can prove none was attempted.
if "%LEAKSCAN_SHIM_LOG%"=="" exit /b 99
>>"%LEAKSCAN_SHIM_LOG%" echo %*
if /i "%~1"=="inspect" goto inspect
exit /b 1
:inspect
if not defined LEAKSCAN_SHIM_INSPECT_NAME exit /b 1
echo %LEAKSCAN_SHIM_INSPECT_NAME%
exit /b 0
