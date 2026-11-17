# Matt's PC Care — Agent

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](./LICENSE)

The Windows client for **Matt's PC Care** — a light-touch PC health service for
home users and small offices, run by [Matt's PC Repair](https://monstermatt2.net).

The agent installs as a small background service and quietly takes care of the
machine, while the owner keeps a friendly app for one-click cleaning and
remote help.

## What it does

- **AI health monitoring** — checks in on a schedule; issues (disabled Windows
  Update, failing services, driver problems, storage/disk warnings) are
  diagnosed by the PC Care service and explained in plain language
- **One-click repair** — when something needs attention, the owner gets a
  Windows notification with *"Tap to let our AI repair it now"* — no ticket,
  no phone call
- **Quick Clean** — selective cleanup of temp files, browser caches, update
  leftovers, memory dumps, and more, with live progress
- **Remote help** — built-in RustDesk support with a per-session client number
  and rotating password, so a technician can help without ever asking the
  owner to read out credentials
- **Self-updating** — new releases install themselves through a verified,
  staged update pipeline (SHA-256 checked, rollback-safe)
- **One-click install** — download, double-click, one button, one Windows
  prompt. The installer requests a one-time key from the server; no codes to
  type, nothing sensitive stored in the binary

## Install

Download the latest `PcCareAgent.exe` from
**https://monstermatt2.net/pcare/PcCareAgent.exe**, double-click, press
**Install on this PC**, and approve the Windows prompt. That's it.

> Windows SmartScreen may warn about an unknown publisher on first run —
> the binary is in the process of getting a signing certificate.

## Build from source

```bash
git clone https://github.com/monstermatt2/pc-care-agent.git
cd pc-care-agent/PcCareAgent
dotnet publish -c Release
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
with Windows targeting (`net8.0-windows10.0.19041.0`).

## How it talks to the service

The agent is only half the product — it speaks to the PC Care API
(`api.monstermatt2.net`) for enrollment, health reports, AI diagnosis,
cleanup tasks, and updates. This repository contains the client only.
Devices enroll with a server-minted one-time key plus a hardware
fingerprint (CPU/board/BIOS/disk serials, hashed), and new installs wait for
owner approval before any AI or maintenance runs.

## Privacy

- Browser history, cookies, and saved passwords are never read or touched
- The agent reports system health facts (CPU/RAM/disk, service states,
  hardware serials hashed) — nothing else
- Remote-access passwords rotate per session and are held server-side only
  for the technician

## License

Copyright (C) 2026 Matt's PC Repair

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU Affero General Public License as published by the Free
Software Foundation, either version 3 of the License, or (at your option) any
later version. See [LICENSE](./LICENSE) for the full text.

This project redistributes [RustDesk](https://rustdesk.com) binaries, which
are licensed under AGPL-3.0.
