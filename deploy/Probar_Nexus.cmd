@echo off
rem Un clic: prueba contra la API de Nexus (pide llave, usuario y contrasena en pantalla).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0prueba-nexus.ps1"
echo.
pause
