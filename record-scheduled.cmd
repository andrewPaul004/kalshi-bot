@echo off
rem Scheduled recording for Windows Task Scheduler: records watched markets and series once,
rem appending output to data\record.log next to this script. Safe to overlap: the recorder lock refuses a second run.
cd /d "%~dp0"
if not exist data mkdir data
echo ==== %DATE% %TIME% >> "%~dp0data\record.log"
call "%~dp0kb.cmd" record >> "%~dp0data\record.log" 2>&1
