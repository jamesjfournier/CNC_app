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

