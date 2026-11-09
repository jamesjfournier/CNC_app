# DBA CNC

Windows desktop app for scribing product numbers onto aluminum plates held in a fixed fixture. It talks to a GRBL 1.1 controller over a USB serial port (CH340) and jogs, plots, and scribes from a work zero the operator sets each session.

Version 1.5. The program does not start the spindle. Scribing is a drag operation after the operator engages the spindle button on the machine.

## Application Walkthrough

### Main Interface Overview
![Main Application Interface](assets/app-main-view.png)

### Features & Settings
<table>
  <tr>
    <td width="50%">
      <p align="center"><b>1. GRBL Controller Connection</b></p>
      <img src="assets/connection-menu.png" alt="Connection Menu" width="100%"/>
    </td>
    <td width="50%">
      <p align="center"><b>2. Scribe Path Plotting</b></p>
      <img src="assets/path-plotting.png" alt="Plotting Section" width="100%"/>
    </td>
  </tr>
  <tr>
    <td width="50%">
      <p align="center"><b>3. Manual Jogging Controls</b></p>
      <img src="assets/manual-jog-preview.png" alt="Jogging and Preview" width="100%"/>
    </td>
    <td width="50%">
      <p align="center"><b>4. Plate Template Selection</b></p>
      <img src="assets/plate-selection.png" alt="Selection Menu" width="100%"/>
    </td>
  </tr>
</table>

## Layout

dba-cnc/
├── .github/            GitHub Actions build, and a bug-report template
├── assets/             Header logo, app icon artwork, sample product CSV
├── docs/               Operator guide
├── src/                The application (one Windows program)
├── .gitignore
├── LICENSE
└── README.md

The application runs entirely from the src folder as a single C# Windows program without a separate server or web frontend.

Requirements

• Windows with .NET Framework 4
• GRBL 1.1 controller connected via USB serial (CH340) at 115200 baud

Build & Run

Run Build.bat inside the src folder to generate src\DBA CNC.exe. Copy the executable to your shop PC alongside optional configuration files like plate-template.txt and product .csv lists. Daily operation instructions are available in docs/user-guide.md.

License

Copyright 2026 DBA. All rights reserved. See LICENSE.
