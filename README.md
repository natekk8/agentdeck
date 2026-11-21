# AgentDeck v0.6 — Remote AI IDE control in your pocket.

AgentDeck lets you launch, monitor, and control **Codex** and **Antigravity** sessions on your Windows PC directly from an **M5Stick S3**. The Windows companion app manages IDE-synced projects, global & project-specific prompts, auto-saved Codex accounts, Antigravity subscription/quota status, live StickS3 battery monitoring, device pairing, Wi-Fi setup, and Remote Control integration.

## Quick Start

Double-click **`AgentDeck.exe`** (no console/CMD flash — automatically starts the silent background bridge) or run `Start-AgentDeck.ps1 -Desktop`. Complete the first-run **Onboarding Wizard** to set your language (**English** or **Polski**), enable silent Windows **Autostart** (for Wake on LAN), and pair your **StickS3**.

On a clean machine, install Python 3.12+, Codex CLI, and Antigravity CLI (`agy`), sign in, then run:

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r host\requirements.txt
.\Start-AgentDeck.ps1 -Desktop
```

## Highlights in v0.6

- **Zero-CMD Windows Launcher & Autostart**: `AgentDeck.exe` and optional Windows login autostart run the Python bridge via `pythonw.exe` without opening a terminal window.
- **First-Run Onboarding Wizard**: Choose English or Polish, enable background autostart for Wake on LAN, and pair your StickS3 via USB in three guided steps.
- **StickS3 Battery Indicator**: Live battery percentage and charging status are sampled by the firmware (`0.6.0`) and displayed in both the StickS3 header bar and the Windows sidebar/Devices list.
- **Intuitive Accounts & Antigravity Subscription Status**: Active Codex accounts are saved automatically for 1-click switching (`Switch` / `Add Codex Account`), while Antigravity displays your active plan tier (`Pro` / `Ultra` / `Standard`), credits, and weekly model quotas.
- **Persistent Background Sessions**: Leaving the task view on StickS3 never interrupts execution on PC; returning to **Sessions** shows live status (`Running` / `Question` / `Completed`) and full paged output.

## Language (English Default + Polish)

- AgentDeck defaults to **English** on both Windows and M5Stick S3.
- Switch between **English** and **Polski** anytime in Onboarding, Windows (**Settings -> Language**), or directly on the Stick (**Settings -> Language**).

## IDE-Synced Projects, Quick New Projects & Global Prompts

- **Codex**: reads registered projects from `~/.codex/.codex-global-state.json` and recent `thread/list` working directories.
- **Antigravity**: reads registered projects from `~/.gemini/config/projects/*.json` and IDE/CLI/ACP workspace indexes.
- **`+ New Project` on StickS3 or Windows**: creates a fresh Git-initialized folder inside your configured projects root (`D:\Projects`) using a random 2-word English slug (`solar-star`, etc.).
- **All Projects (Global) Prompts**: prompts saved under **All Projects (Global)** (`project: "*"`) appear in every project on StickS3.

## Remote Control & Away-From-Home Access

- **Official IDE Remote Control**: manage `codex remote-control` and `agy remote-control` directly from **Remote Control** in Windows.
- **StickS3 Encrypted Relay**: connects your StickS3 over any hotspot using end-to-end **AES-256-GCM + HMAC-SHA256** encryption (`host/relay.py` or built-in relay on port `8793`).

## Firmware & Tests

`AgentDeck-StickS3.bin` (`0.6.0`) is the OTA application image for M5Stick S3 / M5 Launcher.

```powershell
$env:PLATFORMIO_CORE_DIR = 'D:\AgentDeck-tools\platformio'
$env:PLATFORMIO_BUILD_DIR = 'D:\AgentDeck-tools\upgrade-build'
$env:PLATFORMIO_LIBDEPS_DIR = 'D:\AgentDeck-tools\vendor\.pio\libdeps'
& 'D:\AgentDeck-tools\venv\Scripts\python.exe' -m platformio run
& 'D:\AgentDeck-tools\venv\Scripts\python.exe' -m unittest discover -s host -v
```
