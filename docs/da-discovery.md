# OPC DA Server Discovery

The bridge can discover OPC DA servers installed on the **local machine** or on **remote hosts**. When credentials are provided, enumeration runs **under impersonation** so that servers registered in another user's profile are visible.

## How server registration works on Windows

```
 Machine-wide (HKLM)                Per-user (HKCU)
 ─────────────────────              ────────────────
 Installed by admin                 Installed by a user
 Visible to ALL users               Visible to THAT user only

 Registry path:                     Registry path:
 HKLM\SOFTWARE\Classes\CLSID        HKCU\SOFTWARE\Classes\CLSID
 HKLM\SOFTWARE\WOW6432Node\...      HKCU\SOFTWARE\WOW6432Node\...
```

## What the bridge scans

| Credentials provided | Local scan | Remote scan |
|----------------------|------------|-------------|
| **None** | HKLM only (machine-wide) | OpcEnum DCOM (no auth) |
| **Username + password** | HKLM + impersonated user's HKCU | OpcEnum under impersonation |

## Discovery workflow

```
 Sources → OPC DA → Credentials section

  Host: [localhost          ]
  User: [opcuser            ]  ← user who installed the OPC DA server
  Pass: [********           ]
  Domain: [WIN-PC           ]

  [Scan Servers]  ← click to enumerate

 Step 1: LogonUser("opcuser", "WIN-PC", "password", NEW_CREDENTIALS)
         → gets a Windows security token for opcuser

 Step 2: Under impersonation, scan:
         HKLM\SOFTWARE\Classes\CLSID          ← machine-wide servers
         HKCU\SOFTWARE\Classes\CLSID           ← opcuser's per-user servers

 Step 3: Results show BOTH:
         ✅ Matrikon.OPC.Simulation.1    (from HKLM — machine-wide)
         ✅ CustomOPC.Server.1            (from HKCU — opcuser's profile)

 Step 4: Select server → Save Connection
         → same credentials used for COM connection (LogonUser + CreateInstance)
         → COM resolves ProgID from impersonated user's registry
         → server connects successfully
```

## When to use credentials for scanning

- **No credentials needed** — servers installed normally (machine-wide, admin install)
- **Credentials needed** — server installed by a specific user (per-user COM registration), or remote host requiring DCOM authentication
- **Manual ProgID** — if a server doesn't appear in scan, type the ProgID directly in Sources → OPC DA → ProgID field and provide credentials

## Limitations

- The bridge scans only **one user's HKCU** per scan (the user whose credentials you provide)
- It does **not** scan all user profiles on the machine (that would require loading each user's NTUSER.DAT registry hive)
- Remote enumeration requires the `OpcEnum` service running on the target host and TCP port 135 (DCOM) accessible
