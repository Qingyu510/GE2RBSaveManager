[简体中文](README.md) | **English**

# GE2RB Save Manager

A save-file manager and cross-platform converter for *GOD EATER 2 Rage Burst* (Windows, .NET Framework 4.8, WinForms).

## Features

### Save detection & info

Drag a save folder containing `SECURE.BIN` into the window. The tool detects the platform and decrypts it, showing:

| Field | Description |
| --- | --- |
| Save path | The folder you dropped |
| Platform | PS Asia / PS US-EU / Steam |
| Name | The player name stored in the save |
| Codename | The player codename stored in the save |

Platform is detected automatically from markers inside the save — no manual selection needed.

### Platform conversion

Two side-by-side target buttons are shown; they automatically point to the two platforms other than the current source:

- PS Asia ↔ Steam: direct
- PS US-EU ↔ Steam: direct
- PS Asia ↔ PS US-EU: via Steam (two steps)

### Steam user list

Reads `<Steam>\config\loginusers.vdf` and displays each `userdata` folder (SteamID) as `PersonaName (AccountName)`.

### Slot management

Each account has 3 slots, with per-slot rows providing:

- **Slot button** — shows the player name of that slot's save
- **Backup** — copies the slot into `backups\` next to the exe
- **Delete** — removes all files in the slot (with confirmation)
- **Replace** — replaces the slot with the dropped save; non-Steam sources are converted first, then it asks whether to repair vdf

### Misc

- **Repair vdf** — fixes Steam Cloud save state, resolving card loading errors (may require closing Steam first)
- **Open backups** — opens the backup folder
- **Steam path detection** — reads the registry first (`InstallPath` / `SteamPath` / `SteamExe`); if not found or invalid, an Explorer-style folder picker is shown so you can choose it manually
- **Fallback re-pick** — when no valid Steam path is set, clicking "Backup / Delete / Replace / Steam user list" prompts you and re-triggers path selection

## Requirements

- Windows
- Runtime: .NET Framework 4.8
- Build: .NET SDK (capable of targeting `net48`) or Visual Studio 2022

## Build

```bash
dotnet build -c Release
```

Output: `GE2RBSaveManager/bin/Release/net48/GE2RBSaveManager.exe`

## Repository layout

```
.
├── README.md                     # 简体中文
├── README.en.md                  # This file (English)
├── .gitignore
├── LICENSE                       # GNU GPL v3.0 (full text)
└── GE2RBSaveManager/
    ├── GE2RBSaveManager.csproj
    ├── Program.cs                # All logic (GUI + conversion core)
    ├── ps_asia_steam_main.txt    # embedded resource
    ├── ps_asia_steam_card.txt    # embedded resource
    ├── ps_us_steam_main.txt      # embedded resource
    └── ps_us_steam_card.txt      # embedded resource
```

## Embedded resources

The four `*.txt` files below are embedded into the exe via `<EmbeddedResource>` (single-file, self-contained distribution). They hold the structural insert/delete patch data used by platform conversion:

| File | Purpose |
| --- | --- |
| `ps_asia_steam_main.txt` | PS Asia → Steam (main save) |
| `ps_asia_steam_card.txt` | PS Asia → Steam (card save) |
| `ps_us_steam_main.txt` | PS US-EU → Steam (main save) |
| `ps_us_steam_card.txt` | PS US-EU → Steam (card save) |

## License

This project is released under the **GNU General Public License v3.0**. See [LICENSE](LICENSE) for the full terms.

```
Copyright (C) 2026 Qingyu510

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
```

### Credits & third-party sources

- The save **decryption / encryption** implementation references [seulleta/GERSaveCryptTool](https://github.com/seulleta/GERSaveCryptTool).
  That repository currently declares no license, so the GPL-3.0 grant of this project **does not extend to any original code from it**; the reference above is attribution and acknowledgement only. If that repository later declares a license, its own terms take precedence.

## Disclaimer

This tool is intended for personal save management and file-format research only. Back up your saves before use; use at your own risk.
