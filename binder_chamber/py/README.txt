BINDER Climate Chamber Control v3.4
Functions / Tracks + Verified Stored-Program Control

SOURCE BASIS
BINDER Interface Technical Specifications, issue 10/2017.

NETWORK
Default IP: 192.168.3.137
TCP port: 10001
Modbus slave address: 1

MB2 REGISTERS USED
Read:
0x1004  Actual temperature
0x100A  Actual humidity
0x10B2  Temperature setpoint
0x10B4  Humidity setpoint
0x10B6  Fan setpoint
0x1292  Track read (bits 0..15)

Read/Write:
0x114C  Manual temperature
0x114E  Manual humidity
0x1150  Manual fan
0x1158  Track manual (bits 0..15)
0x1147  Program number
0x1148  Start section number
0x1149  Start program (write 1)
0x114A  Stop program (write 1)
0x114B  Pause program (write 1)

FUNCTIONS / TRACKS
The Functions / Tracks tab:
- Reads Track manual 0x1158.
- Reads Track read 0x1292.
- Shows all 16 bits.
- Lets the user edit 0x1158 with checkboxes.
- Requires Enable write controls.
- Confirms before writing.
- Reads 0x1158 back and verifies the exact written value.

Known labels shown in the GUI follow the visible order in the MKF/MKFT
Functions on/off menu:
bit 0  Humidity off
bit 1  Idle mode
bit 2  Switching output 1
bit 3  Switching output 2
bit 4  Switching output 3
bit 5  Switching output 4
bit 6  Anti-condensation
bit 7  Compressed air valve
bit 8  Compressed air dryer

Bits 9..15 use generic Track labels because the 2017 MB2 interface table
only documents Track 0..15 and does not provide those human-readable names.

STORED PROGRAM CONTROL
Stored-program controls have an independent safety enable.

START sequence:
1. Convert displayed program 1..30 to documented register value 0..29.
2. Write Program number 0x1147.
3. Read 0x1147 back and require an exact match.
4. Write the user's RAW start-section value to 0x1148.
5. Read 0x1148 back and require an exact match.
6. Only then write 1 to START 0x1149.

If 0x1148 times out or verification fails, START 0x1149 is NOT sent.

START SECTION NOTE
The 2017 interface table names 0x1148 "Start section number", but does not
state whether numbering is zero-based or one-based. v3.4 sends exactly the
integer entered by the user and does not guess.

STOP
Writes 1 to 0x114A.

PAUSE
Writes 1 to 0x114B.

OTHER FEATURES RETAINED
- Live monitoring
- Dual-axis temperature/humidity chart
- CSV logging
- Manual STEP control
- Software RAMP
- PC-side Sequence Controller
- Save/load sequences
- Raw protocol logging
- Local program labels

SAFETY
Any write can affect the real chamber and test specimen.
Use low-risk settings for initial verification.
