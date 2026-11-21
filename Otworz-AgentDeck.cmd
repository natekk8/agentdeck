@echo off
if exist "%~dp0AgentDeck.exe" (
    start "" "%~dp0AgentDeck.exe"
) else (
    powershell.exe -NoProfile -WindowStyle Hidden -File "%~dp0Start-AgentDeck.ps1" -Desktop
)
