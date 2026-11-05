# DBA CNC operator guide

Version 1.5. Use this with the machine in front of you. The app will not move an axis until a button that moves the machine is pressed, and those buttons ask first.

## Connect

1. Plug in the controller and choose its COM port. Baud is 115200 unless the controller was set otherwise.
2. Press **Connect**. The button turns into **Disconnect** while the port is open.
3. Status should settle to Idle. **Unlock** clears an alarm lock if the controller reports one.

Closing the app while it is connected can reset the controller. Disconnect first when you are done, and do not close it in the middle of a job.

## Set home

Home is the template mark for this session, not a position stored from last time.

1. Jog to the mark. Arrow keys move X and Y. Page Up and Page Down move Z. Step size is 0.1, 1, or 5 mm. Z steps stay at 2 mm or less.
2. Press **Set Home** and confirm. X, Y, and Z then read 0.

**Go Home** raises the bit, moves X back to 0, then Y back to 0, then lowers to Z 0. It does not cut the corner diagonally.

**Load plates** raises Z to 15 mm above home and leaves X and Y where they are. If Z is already that high, it does nothing.

## Plot the plates

The fixture is 4 rows of 2 plates. Row 1 is the bottom pair. The machine only reaches row 4.

On row 1, plot the left plate (top-left, bottom-left, right edge, and Z surface) and the right plate (top-left and its own Z). Height and width of the right plate copy from the left.

On rows 2 through 4, plot only the left plate's top-left. The other X and Y points copy from row 1, shifted up to that corner. Z is not copied. Each plate on each row has its own Z surface.

A red tile will be scribed. White will not. Click **L** or **R** to toggle that plate and view the row. Click the row number only to view that row. The **Plates** box selects the first N plates from the bottom left, going left to right and then upward.

## Product number

The box is labeled with the row you are viewing. Left and right on that row share one number. **Apply to all** copies the current text onto every row. Uncheck it to give a row its own number.

Letters, digits, hyphens, and spaces are allowed. Lowercase is scribed as uppercase. A hyphen is scribed. A space is an empty cell.

**Re-do last** scribes only the last N characters, in their original place on the plate. If N is longer than a selected row's text, Start refuses that job.

Depth is 0.0025 mm through 0.0100 mm, in steps of 0.0025. Lift is the height the bit travels at between strokes.

## Start

**START** asks two questions. Cancel on the first one stops. Closing the second window does not start.

1. Ready to scribe, plates loaded, film removed.
2. Green spindle button engaged, and the machine clear.

The timer runs from the second answer until the job finishes or **STOP** is pressed. **PAUSE** holds the feed. **RESUME** continues. **STOP** aborts the scribe.

Between strokes, and when traveling up to a higher row, the machine rapids. It goes to X 0 before moving up in Y. At the end of a finished job it returns home on X, then Y, then Z. Stop does not send it home.

## Other controls

- **Display** scales the window. Fit screen is the default. The choice is remembered.
- **Export** and **Import** save and load the scribe settings. Import does not replace Set Home for the current session.
- The **Console** tab is the message log.

## Keyboard

Arrow keys jog X and Y. Page Up and Page Down jog Z. Esc stops. Left and Right do not jog while the cursor is in the product box, the feed box, or a number box.
