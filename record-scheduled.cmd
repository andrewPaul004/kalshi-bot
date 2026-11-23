@echo off
rem Scheduled recording for Windows Task Scheduler: records watched markets and series once, and the NWS
rem Central Park forecast at most hourly, appending output to data\record.log. Overlaps are refused by the recorder lock.
cd /d "%~dp0"
if not exist data mkdir data
echo ==== %DATE% %TIME% >> "%~dp0data\record.log"
call "%~dp0kb.cmd" record >> "%~dp0data\record.log" 2>&1
call "%~dp0kb.cmd" forecast collect --min-interval 55 >> "%~dp0data\record.log" 2>&1
