@echo off
rem Starts the Stars wallpaper helper without a console window (needs Python 3 from the Microsoft Store).
cd /d "%~dp0"
start "" pythonw stars_helper.py
