@echo off
chcp 65001 >nul
title SMM Server

echo ========================================
echo    ЗАПУСК SMM СЕРВЕРА
echo ========================================

:: Переход в папку проекта
cd /d H:\gitfun\project\smm2

:: Проверка, что Python работает
py --version >nul 2>&1
if errorlevel 1 (
    echo [ОШИБКА] Python не найден!
    echo Установите Python или проверьте путь.
    pause
    exit /b
)

echo [1] Запуск сервера...
echo     Адрес: http://localhost:8000
echo.

:: Запуск сервера (команда видна, чтобы видеть ошибки)
py -u -m uvicorn main:app --host 0.0.0.0 --port 8000

:: Если сервер упал - покажем ошибку
echo.
echo [ОШИБКА] Сервер остановлен.
pause