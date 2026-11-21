# AgentDeck (v1 Preview)

Zdalne sterowanie AI IDE w Twojej kieszeni.

AgentDeck łączy komputer z Windows z urządzeniem M5Stick S3, dzięki czemu możesz uruchamiać prompty dla Antigravity i Codex CLI, podglądać postęp pracy agentów, odpowiadać na pytania w trakcie sesji oraz przełączać konta nawet wtedy, gdy odejdziesz od biurka.

## Co znajdziesz w aplikacji

- **Projekty i prompty** z automatycznym wykrywaniem workspace z Antigravity i Codex CLI oraz gotowymi szablonami promptów.
- **Sesje na żywo** pozwalające podejrzeć odpowiedź agenta, status wykonania, pytania wymagające wyboru i pełny log po powrocie do komputera.
- **Konta i subskrypcje** z podglądem aktywnego planu i limitów Antigravity oraz szybkim przełączaniem kont Codex CLI.
- **Urządzenia i Wi-Fi** z parowaniem StickS3 przez USB-C jednym kliknięciem, podglądem baterii w procentach i synchronizacją ustawień między Windows a Stickiem.
- **Remote Control i przekaźnik** do bezpiecznego sterowania IDE oraz pracy poza domową siecią Wi-Fi.
- **Auto-Updater i aktualizacje StickS3** z paskiem pobierania z GitHub Releases, cichą instalacją w tle i przypomnieniem o podłączeniu Sticka przez USB, gdy firmware wymaga aktualizacji.
- **Zgłoszenia (Feedback)** do szybkiego wysyłania błędów i propozycji funkcji na GitHub przez Sentry.io.

## Szybki start na Windows

1. Pobierz i uruchom instalator `AgentDeck-Setup-1.0.0.exe` z zakładki Releases.
2. Uruchom **AgentDeck**. Przy pierwszym starcie kreator pomoże Ci wybrać język (polski lub angielski), włączyć cichy autostart w tle (bez okien CMD, potrzebny m.in. do Wake on LAN) oraz sparować StickS3.
3. Podłącz M5Stick S3 kablem USB-C, jeśli aplikacja poprosi o parowanie lub wgranie nowej wersji firmware.

## Struktura repozytorium

- `host/`: aplikacja desktopowa WPF (`Window.xaml`, `Desktop.cs`), lokalny mostek Python (`agentdeck.py`, `links.py`, `updater.py`, `sessions.py`, `accounts.py`) oraz testy (`test_agentdeck.py`).
- `firmware/`: firmware dla M5Stick S3 (ESP-IDF / Arduino / PlatformIO).
- `installer/`: skrypty budowania `AgentDeck.exe` i instalatora Inno Setup (`AgentDeck-Setup-1.0.0.exe`).
- `relay/`: serwer przekaźnika WebSocket (`relay.py`).
- `assets/`: ikony i zasoby wizualne aplikacji.

## Testy i budowanie lokalne

Uruchomienie testów jednostkowych mostka i auto-updatera:

```powershell
python -m unittest discover -s host -v
```

Budowanie aplikacji i instalatora Windows:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```
