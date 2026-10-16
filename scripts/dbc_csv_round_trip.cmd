@echo off
rem A data table through a spreadsheet and back (WoW Data Studio command line, wds).
rem
rem   dbc_csv_round_trip.cmd <Table> [csv file] [apply]
rem
rem   1. without the CSV file existing: exports the table to the CSV file (default: <Table>.csv) and stops;
rem      edit it in LibreOffice Calc or Excel and save it as CSV (UTF-8);
rem   2. with the CSV file there: shows what the import would change (and the errors that would block it);
rem   3. with "apply" as the third argument: imports it and saves the table into your workspace (My changes).
rem
rem   Set WDS to the wds program and WDS_HOME to a --home folder when needed. The exit code is wds's (0 ok, 3 the
rem   file has errors, ...; see "wds --help").
setlocal
if "%~1"=="" (
  echo Usage: %~nx0 ^<Table^> [csv file] [apply]
  exit /b 2
)
if "%WDS%"=="" set WDS=wds
set HOMEOPT=
if not "%WDS_HOME%"=="" set HOMEOPT=--home "%WDS_HOME%"
set TABLE=%~1
set CSV=%~2
if "%CSV%"=="" set CSV=%TABLE%.csv

if exist "%CSV%" goto import
"%WDS%" %HOMEOPT% dbc export "%TABLE%" --out "%CSV%"
if errorlevel 1 exit /b %errorlevel%
echo.
echo Edit %CSV% and save it as CSV (UTF-8), then run: %~nx0 %TABLE% "%CSV%"
exit /b 0

:import
if /i "%~3"=="apply" goto apply
"%WDS%" %HOMEOPT% dbc import "%TABLE%" "%CSV%"
if errorlevel 1 exit /b %errorlevel%
echo.
echo To import it: %~nx0 %TABLE% "%CSV%" apply
exit /b 0

:apply
"%WDS%" %HOMEOPT% dbc import "%TABLE%" "%CSV%" --apply
exit /b %errorlevel%
