@echo off
rem WoW Data Studio command prompt (Start menu): the wds command line on the PATH of this window only.
set "PATH=%~dp0;%PATH%"
cd /d "%USERPROFILE%"
echo WoW Data Studio command line
wds version
echo.
echo Type  wds --help  for the commands,  wds ^<group^> --help  for one group (setup, find, textures, models, maps, server-maps, dbc, changes).
echo The manual: Help ^> The command line (wds) in the app, or  wds manual command-line --open
