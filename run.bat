@echo off
setlocal
cd /d "%~dp0"
title Google Flow Desktop (dGPU Accelerated)
echo ========================================================
echo   Google Flow Windows Desktop (C# Native Edition)
echo   Hardware Acceleration: NVIDIA GeForce GTX 1650
echo   Bypass Engine: Google Labs Country Unlocker v4.1
echo ========================================================
echo Starting GoogleFlowDesktop.exe...
start "" "%~dp0GoogleFlowDesktop.exe"
exit /b 0
