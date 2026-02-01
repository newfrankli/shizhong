@echo off
echo 查看错误日志文件内容：
echo ======================
type "%APPDATA%\WeatherClock\error.log"
echo ======================
echo 按任意键退出...
pause > nul