@echo off
setlocal
cd /d H:\gitfun\project\smm2

ollama list >nul 2>&1
if errorlevel 1 (
    echo Ollama не запущен. Запускаю Ollama...
    start "Ollama" /B ollama serve
    timeout /t 3 /nobreak >nul
)

start "" http://localhost:8000
py -m uvicorn main:app --host 0.0.0.0 --port 8000
