# PLC Drivers (Mitsubishi A3N)

The bridge can poll a Mitsubishi **A3NCPU** over **RS-232** using MELSEC **A-compatible 1C Frame**
(Dedicated Protocol / Format 1).

1. Open **Connectivity → Drivers** and add a Mitsubishi A3N driver.
2. Set the serial port (e.g. `/dev/ttyUSB0`), baud **9600**, **8 data bits**, **odd parity**, **1 stop bit** (match the PLC).
3. Map tags with device addresses: `D100`, `M10`, `X20`, `Y0F`, bit-in-word `D100:8`.
4. Writes on writeable tags go back to the PLC. Bit-in-word uses read-modify-write.

This is separate from **OPC DA** sources and from this process’s **OPC UA server** endpoint.



# PLC Drivers (Siemens S7-200 PPI)

The bridge can poll a Siemens **S7-200** over a host **PPI** serial cable (pure managed client).

1. Open **Connectivity → Drivers** and add a Siemens S7-200 driver.
2. Set serial port (e.g. `/dev/ttyUSB0`), defaults **9600 8E1**, Local PPI **0**, Remote PPI **2**.
3. Map tags with Siemens addresses: `I0.0`, `Q0.1`, `M10.2`, `VB10`, `VW100`, `VD200`.
4. Poll-only ingest; write-through when a mapping is Writeable.
