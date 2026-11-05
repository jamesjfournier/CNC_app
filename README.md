# DBA CNC

Windows desktop app for scribing product numbers onto aluminum plates held in a fixed fixture. It talks to a GRBL 1.1 controller over a USB serial port (CH340) and jogs, plots, and scribes from a work zero the operator sets each session.

Version 1.5. The program does not start the spindle. Scribing is a drag operation after the operator engages the spindle button on the machine.

## Layout

```
dba-cnc/
├── .github/            GitHub Actions build, and a bug-report template
├── assets/             Header logo, app icon artwork, sample product CSV
├── docs/               Operator guide
├── src/                The application (one Windows program)
├── .gitignore
├── LICENSE
└── README.md
```

There is no separate server and no web frontend. `src` holds the whole program: the window, the serial link, and the scribe path are one C# file.

## Requirements

- Windows with .NET Framework 4 (the compiler that ships with Windows is enough)
- A GRBL 1.1 controller on a USB serial port, 115200 baud
- The port is chosen in the app. It is not hard-coded

## Build

From the `src` folder:

```bat
Build.bat
```

That writes `src\DBA CNC.exe`. The header logo and the app icon are embedded from `assets` at compile time, so a replaced logo is not visible until the next build.

GitHub Actions runs the same build on Windows and uploads the exe as an artifact.

## Run

Copy `DBA CNC.exe` to the folder you want to use on the shop PC. Two optional files live beside the exe and are created or read there:

| File | Purpose |
| --- | --- |
| `plate-template.txt` | Last plate plots, depth, feed, and display size. Written by the app. |
| Any `*.csv` | Product-number autocomplete. First column, or a column whose header contains product, part, item, or sku. |

`assets/sample-product-list.csv` shows the shape of that file. The shop's real product list and `plate-template.txt` are left out of this folder on purpose. They are machine and job data, and `.gitignore` keeps them out of git.

Connecting opens the COM port. On most CH340 boards that also resets the controller, so the reported position becomes zero wherever the gantry already is. Set Home again every session after jogging to the template mark. The app does not move to a saved home when it connects.

Day-to-day operation is in [docs/user-guide.md](docs/user-guide.md).

## License

Copyright 2026 DBA. All rights reserved. See [LICENSE](LICENSE).
