@echo off
cd /d "%~dp0"
call "%~dp0build-installer.bat"
exit /b %errorlevel%
