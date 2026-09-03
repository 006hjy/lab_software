# **4. BINDER TCP/IP communication with the BINDER Controller World (BCW T4.x)** 

## **4.1 UDP General** 

Always Port 9001 

All read and write telegrams must end with CRLF (0x0D0A). Otherwise CRLF must not appear in a telegram. 

### **4.1.1 Detecting T4.x Ethernet devices in the network (UDP)** 

#### **Communication:** 

1. Chamber detection (UDP IP:255.255.255.255): 

2. Computer sends "GetConfigDirect" (ASCII format) 

3. T4.x answers with chamber information (Timeout:3000ms + max. 500 characters per answer): 

"IP:" + strIP + " Port:" + strTCPPort +" MAC:" + strMAC + TAB + 

"Chambertype:strChamberType" + TAB + 

"Controllertype:strControllerType" + TAB + 

- "COMType:BCWDirect" + TAB + 

"ProgramController:strProgramController"; 

Explanation of the C# Notation: 

strIP = IP address of the chamber as string (xxx.xxx.xxx.xxx) 

strTCPPort = UDP Port number decimal as string 

strMAC = "xx-xx-xx-xx-xx-xx" as string 

strChamberName = Chamber (default), otherwise customer’s setting on the T4.x (max 40 characters) 

strChamberType = Examples: "CB 160 (E6)", "KT 115 (E6)"... (max 250 characters, no special characters) 

strControllerType = "T4.2", "T4.1" .... (max 250 characters and ".", no special characters) 

strProgramController = "True" or "False" 

TAB = ASCII for [TAB] 

CRLF = ASCII for [CR][LF] 

## **4.2 TCP General** 

Always Port 9000. 

If writing the value, the value returns as data (corrected, if appropriate) 

### **4.2.1 Connecting a chamber via TCP – general** 

All read and write telegrams must end with CRLF (0x0D0A), in either direction. 

Connection established by computer through port 9000; 4 trials each with a 1000 ms timeout 

1. Computer sends command to T4.x (e.g. communication test: "CANIDGetValue:10010010"+CRLF); timeout:1000 ms 

2. T4.x transmits byte counts of data to the computer; timeout 1000ms 

3. T4.x transmits data to the computer; timeout 1000ms 

Before T4.x sends data, it transmits a telegram with the data byte counts (decimal , max 100 characters) to the computer. This telegram always must end with CRLR. 

#### **Structure:** 

For transmission, the maximum byte count is100 characters (e.g. 23789 Bytes = 5 characters) 

All telegrams have the structure "command:command value“. They are separated by the colon. 

If a value is included in the telegram, it is also separated by a colon: "command:command value:value" 

The answer is always "command:command value". If a value string is included, it is separated by a colon. 

The T4.x answers in case of an error (any error) with the key word "Error" as command. 

#### **CANID information:** 

The computer can request information of CANIDs (ID0xYYYYYYYY, with “Y” meaning CANID in HEX) 

<u>Example: CANID ID0x11800080 (Actual temperature value of T4.x)</u> 

1. Computer sends to T4.x: "BCWDirectGetCANIDInfo:ID0x11800080" 

2. T4.2 answers to computer: 

   - "BCWDirectGetCANIDInfo:ID0x11800080:" 

   - + "Befehl=ID0x11800080" + CRLF 

   - + "HexAdress=11800080"  + CRLF 

   - + "VarType=float" + CRLF 

   - + "ReadWords=" + CRLF 

   - + "ConstValue=" + CRLF 

   - + "LimitLow=123.0" + CRLF 

   - + "LimitHigh=600.0"  + CRLF 

- +"ReadWrite=RO" + CRLF 

+ "Doku=P1_ChamberTemperature" + CRLF 

- + "Unit=1" 

Wireshark Raw Data of the answer: 

42 65 66 65 68 6c 3d 49 44 30 Befehl=ID0 

78 31 31 38 30 30 30 38 30 0d 0a 48 65 78 41 64 x11800080..HexAd 

72 65 73 73 3d 31 31 38 30 30 30 38 30 0d 0a 56 ress=11800080..V 

61 72 54 79 70 65 3d 66 6c 6f 61 74 0d 0a 52 65 arType=float..Re 

61 64 57 6f 72 64 73 3d 0d 0a 43 6f 6e 73 74 56 adWords=..ConstV 

61 6c 75 65 3d 0d 0a 4c 69 6d 69 74 4c 6f 77 3d alue=..LimitLow= 

31 32 33 2e 30 0d 0a 4c 69 6d 69 74 48 69 67 68 123.0..LimitHigh 

3d 36 30 30 2e 30 0d 0a 52 65 61 64 57 72 69 74 =600.0..ReadWrit 

65 3d 52 4f 0d 0a 44 6f 6b 75 3d 50 31 5f 43 68 e=RO..Doku=P1_Ch 

61 6d 62 65 72 54 65 6d 70 65 72 61 74 75 72 65 amberTemperature 

0d 0a 55 6e 69 74 3d 31 0d 0a ..Unit=1.. 

### **4.2.2 TCP communication test** 

1. Computer sends to T4.x: "CANIDGetValue:10010010" 

2. If OK, T4.x answers to computer: 

"CANIDGetValue:10010010:Communication OK". 

Otherwise (in case of any error) T4.x answers with the key word "Error". 

### **4.2.3 Reading a CANID value via TCP** 

1. Computer sends to T4.x: "CANIDGetValue:CANIDin32BitHex" 

2. If OK, T4.x answers to computer: 

“CANIDGetValue:CANID in 32Bit Hex:value in string format”. 

Otherwise (in case of any error) T4.x answers with the key word "Error". 

### **4.2.4 Writing a CANID value via TCP** 

1. Computer sends to T4.x: "CANIDWriteValue:CANID in 32Bit Hex:value in internal format" 

2. If OK, T4.x answers to computer: "CANIDWriteValue:CANID in 32Bit Hex:value in internal format " 

Otherwise (in case of any error) T4.x answers with the key word "Error". 

### **4.2.5 Types of variables (internal format)** 

|**Data format**|**Representation (always 32bit)**|
|---|---|
|unsignedbyte|only LSB evaluated AND mask =0x000000FF|
|signedbyte|only LSB evaluated AND mask =0x000000FF|
|float|acc. to IEEE 754|
|longsignedint|-2147483648 up to 2147483647|
|longunsignedint|0 up to 4294967295|
|binary|00000000 00000000 00000000 00000000<br>up to<br>11111111 11111111 11111111 11111111|
|binary16|00000000 00000000 00000000 00000000<br>up to<br>00000000 00000000 11111111 11111111|
|hex|0000 0000 up to FFFF FFFF (longunsignedint)|
|hex16|0000 0000 up to 0000 FFFF (unsignedint)|
|boolean|LSB is decisive (AND mask 0x00000001)|
|enum|see _unsignedint|
|string|Maximum 40 characters,|
|date|Date: always yyyymmdd in longsignedint as a decimal value (yyyymmdd)<br>Example: 23.03.2007 = 2007 = year, 03 = month, 23 = day. Results in<br>20070323 in the telegram.|
|timeabs|Time: hhmmsscc<br>2 characters each for hours, minutes, seconds, and<br>milliseconds as a decimal value (longunsignedint)<br>8 characters include milliseconds. When 6 characters, the milliseconds<br>are set to zero. Internal format: always 24 hours.<br>Example: 8 characters: 23:45:38,83 = 23453883<br>Example: 6 characters: 23:45:38,00 = 234538|
|version|xxxxyyzz one Hex value per Byte (longunsignedint)<br>Version numbers for hardware and software<br>xxxx = Byte<3><2>: Main version numbers 0x0001 up to 0xFFFF<br>(0x0000 = reserved = no information)<br>yy = Byte<1> subversion (0x00 = Beta version, for development only)<br>zz = Byte<0> Bugfix counter|



|**Data format**|**Representation (always 32bit)**|
|---|---|
|seconds|Count of seconds (= longunsignedint)|
|signedint|AND mask = 0x0000 FFFF|
|unsignedint|AND mask = 0x0000 FFFF|
|partno|BINDER number xxxx-xxxx|
||e.g. BINDER number = 5014-01A7Decimal value = 1343488423|
|CANID|Contains a CANID (29 bit) AND mask 1FFF FFFF|
|Int16Div10|16 Bit Integer (value divided by 10 results in real value)|



### **4.2.6 Units** 

|**Parameter**|**unit**|**Unit:unit number**|
|---|---|---|
|none|none|0 (default)|
|Temperature|Kelvin|1|
|Percent|none|2|
|Time relative|seconds|3|
|Pressure|mbar|4|
|Light|SI unit|5|
|Voltage|Volt|6|
|Current|Ampere|7|
|Power|Watt|8|
|Volume Percent|none|9|
|TemperatureDelta|Kelvin|10|
|PercentDelta||11|



### **4.2.7 CANID Table** 

|**Value**|**CAN ID (hex)**|
|---|---|
|All (if function is available)||
|Door open, closed|0x10010100 (Read Only)|
|**KT**||
|Temperature, actual value|0x11400080 (Read Only)|
|Temperature, set-point value|0x114000C0 (Read/Write)|
|Fan speed, set-point value|0x112000E1 (Read/Write)|
|**CB**||
|Temperature, actual value|0x11400080 (Read Only)|
|Temperature, set-point value|0x114000C0 (Read/Write)|
|CO2, actual value|0x11600B07 (Read Only)|
|CO2, set-point value|0x116000C9 (Read/Write)|
|O2, actual value|0x11600082 (Read Only)|
|O2, set-point value|0x116000CC (Read/Write)|
|Humidity, actual value|0x100C1066 (Read Only)|
|Sterilization Active (No = 0, Yes = 1)|0x10010102 (Read Only)|
|**AI**||
|Temperature, actual value|0x11400080 (Read Only)|
|Temperature, set-point value|0x114000C0 (Read/Write)|
|Humidity, actual value|0x11600083 (Read Only)|
|Humidity, set-point value|0x116000C6 (Read/Write)|



