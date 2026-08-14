# OPC DA over DCOM — Configuration Guide for a Windows 10 Workgroup (DESKTOP-3QEUJIV → DESKTOP-BC2AU7H)

Scope: two Windows 10 machines on `192.168.48.x`, **no domain**. The OPC DA client runs on
`DESKTOP-3QEUJIV`; the OPC DA server (in a VM) runs on `DESKTOP-BC2AU7H`. Everything below is
research-derived from primary sources (Microsoft Learn / Microsoft Support, OPC Foundation, and the
canonical Matrikon / Kepware DCOM guides); every claim carries its source inline. Nothing in this
document executes on either machine — it is a configuration reference for an engineer at the console.

> Why this is hard at all: OPC Classic (OPC DA) is defined on Microsoft COM/DCOM, so a remote OPC DA
> connection *is* a DCOM activation plus a stream of DCOM calls, and every one of the many security
> layers can return `0x80070005 E_ACCESSDENIED` ([OPC Foundation — Classic](https://opcfoundation.org/about/opc-technologies/opc-classic/), [Microsoft — Security in COM](https://learn.microsoft.com/en-us/windows/win32/com/security-in-com)).

---

## 1. DCOM activation flow — what happens, and where each security check lives

### 1.1 Two distinct security gates

COM security rests on **authentication** (who is calling) and **authorization** (is the caller
allowed), and splits into two kinds of security ([Microsoft — Security in COM](https://learn.microsoft.com/en-us/windows/win32/com/security-in-com)):

- **Activation security** (a.k.a. launch security): "determines whether a client can launch a server
  at all."
- **Call security**: "After a server has been launched, you can use call security to control access
  to a server's objects."

Launch vs. access permissions are formally distinct: "A launch permissions ACL asserts who is allowed
to start a COM server. An access permissions ACL asserts who is allowed to activate a COM object or
call that object once the COM server is already running"
([Microsoft — DCOM Security Enhancements in XP SP2 / 2003 SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).

### 1.2 The flow, step by step

The OPC client calls `CoCreateInstanceEx(CLSID, …, COSERVERINFO{pServerInfo})` — the variant that
creates an instance "on a specified remote computer"; `pServerInfo` carries the machine name plus a
`COAUTHINFO` with the credentials/authentication settings used **during activation only**
([Microsoft — CoCreateInstanceEx](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstanceex), [Microsoft — COAUTHINFO](https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-coauthinfo)).
`CoCreateInstanceEx` internally does `CoGetClassObject` → `IClassFactory::CreateInstance` →
`IClassFactory::Release` ([Microsoft — CoCreateInstanceEx](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstanceex)).

```mermaid
sequenceDiagram
    participant C as Client DESKTOP-3QEUJIV
    participant EM as Server RPC Endpoint Mapper (TCP 135)
    participant R as RPCSS / SCM (DESKTOP-BC2AU7H)
    participant S as OPC DA server process
    C->>EM: 1. OXID resolver / string binding lookup (TCP 135)
    C->>R: 2. Remote activation request (CoCreateInstanceEx)
    R->>R: 3. AccessCheck #1 — MachineLaunchRestriction (machine-wide launch/activate limit)
    R->>R: 4. AccessCheck #2 — AppID LaunchPermission (else DefaultLaunchPermission)
    R->>R: 5. DCOM hardening gate — activation authn level >= PKT_INTEGRITY
    R->>S: 6. Launch server process under its Identity (RunAs / service / interactive)
    S-->>R: 7. Server registers class factory; R returns OXID + dynamic endpoint (TCP 49152-65535)
    C->>S: 8. Bind to server endpoint; AccessCheck #3 — MachineAccessRestriction
    S->>S: 9. AccessCheck #4 — AppID AccessPermission / DefaultAccessPermission (or CoInitializeSecurity ACL)
    C->>S: 10. IOPCServer calls (read/write/browse/callbacks)
```

1. **Port 135 (RPC Endpoint Mapper).** DCOM uses RPC; the endpoint mapper on TCP 135 tells the
   client which dynamic port the server object lives on. The dynamic ports are allocated from
   49152–65535 on Vista/Server 2008 and later (1025–5000 on XP/2003)
   ([Microsoft — Service overview and network port requirements, KB 832017](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/service-overview-and-network-port-requirements)).
2. **The SCM performs activation security.** "Activation security … is automatically applied by the
   service control manager (SCM) of a particular computer. Upon receipt of a request from a client
   to activate an object … the SCM checks the request against activation-security information stored
   within its registry. (Activation security is also checked for same-computer activations.)"
   ([Microsoft — Activation Security](https://learn.microsoft.com/en-us/windows/win32/com/activation-security)).
3. **Machine-wide limit first.** Since XP SP2 / 2003 SP1 there is "an additional `AccessCheck` call
   that is done against a computer-wide access control list (ACL) on each call, activation, or
   launch of any COM server on the computer … In effect, it provides a minimum authorization
   standard that must be passed to access any COM server on the computer." A principal **denied at
   this level cannot be granted access by any application-specific setting**:
   "Principals not given permissions here cannot obtain them even if the permissions are granted by
   the DefaultAccessPermission registry value or by the CoInitializeSecurity function"
   ([Microsoft — SP2/2003SP1 DCOM enhancements](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1),
   [Microsoft — MachineLaunchRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machinelaunchrestriction),
   [Microsoft — MachineAccessRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machineaccessrestriction)).
4. **Per-AppID launch check.** The SCM evaluates the class's `LaunchPermission` ACL "**while
   impersonating the client**"; if the AppID has no `LaunchPermission`, `DefaultLaunchPermission`
   is used instead ([Microsoft — LaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/launchpermission),
   [Microsoft — DefaultLaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultlaunchpermission)).
5. **Which identity the SCM checks** depends on the client's cloaking flag: "activation examines the
   cloaking flag set in the client's call to CoInitializeSecurity. If the cloaking flag is set (for
   either dynamic or static cloaking), the thread token is used, if present, to determine the
   identity of the client. If no cloaking is set, the process token is used instead of the thread
   token" ([Microsoft — Activation Security](https://learn.microsoft.com/en-us/windows/win32/com/activation-security)).
6. **Server launch under its Identity.** The server process is started under the AppID's configured
   identity — "Interactive User" (the console user), a named account (`RunAs`, needs **Log on as a
   batch job**), or the service accounts (`nt authority\localservice`, `nt authority\networkservice`,
   `nt authority\system`) ([Microsoft — RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)).
7. **Access checks on the running server.** Once the server is up, connecting to and calling it is
   gated by the computer-wide `MachineAccessRestriction` and by the server's `AccessPermission`
   (or `DefaultAccessPermission`, or the ACL passed to `CoInitializeSecurity`). "The COM runtime in
   the server checks the ACL … while impersonating the caller that is attempting to connect to the
   object" ([Microsoft — AccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/accesspermission),
   [Microsoft — DefaultAccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultaccesspermission)).
   Note the SP2+ split into **local vs. remote** rights: `COM_RIGHTS_EXECUTE_LOCAL/REMOTE` and
   `COM_RIGHTS_ACTIVATE_LOCAL/REMOTE` ([Microsoft — SP2/2003SP1 DCOM enhancements](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).
8. **Callbacks are a second, reverse activation.** OPC DA uses a client-side callback sink (the
   client is itself a COM server for `IOPCDataCallback`); the OPC server activates it back on the
   client machine, so the **client machine must also satisfy launch/access checks and firewall
   rules**. The Kepware guide is explicit: "Aside from the server computer, the firewall must also
   be set on client computer so that callbacks can be received"
   ([Kepware — Remote OPC DA (DCOM) Quick Start Guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf),
   archived [copy](https://web.archive.org/web/20220120022013/https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).

### 1.3 Where `0x80070005 E_ACCESSDENIED` can originate — by layer

| # | Gate | Checked by | Typical trigger |
|---|------|-----------|-----------------|
| 1 | `MachineLaunchRestriction` (`HKLM\SOFTWARE\Microsoft\Ole`) | SCM on server | Caller lacks **Remote Launch / Remote Activation**; by default only Administrators (and on Server, Distributed COM Users) have them |
| 2 | Per-AppID `LaunchPermission` / `DefaultLaunchPermission` | SCM on server | The user/group is not in the AppID ACL with `COM_RIGHTS_EXECUTE` |
| 3 | DCOM hardening (authn level < `RPC_C_AUTHN_LEVEL_PKT_INTEGRITY`) | SCM on server | Legacy OPC client requests activation at too low an authentication level |
| 4 | Server launch under Identity | SCM / SCM-created process | Identity = "The interactive user" and nobody is logged on; RunAs account lacks batch logon |
| 5 | `MachineAccessRestriction` | RPCSS on server | Caller denied remote call access at the machine level |
| 6 | Per-AppID `AccessPermission` / `DefaultAccessPermission` / `CoInitializeSecurity` ACL | Running server | Default `DefaultAccessPermission` is **empty** — "Only the server principal and system are allowed to call the server" |
| 7 | Client proxy identity | Client | Impersonation without cloaking (server sees process token, not the impersonated user); wrong/absent `COAUTHIDENTITY`; NTLM impersonation level |
| 8 | Anonymous/Guest token | Server LSA | Credentials didn't authenticate; local-account network logon mapped to Guest; anonymous token lacks the Everyone SID |

Each numbered row above maps to a fix in §4.

### 1.4 Modern wrinkle: DCOM hardening (CVE-2021-26414)

Since 2021–2023, Windows enforces **PKT_INTEGRITY or higher for DCOM activation**. Timeline
(phase 1: June 2021, opt-in; phase 2: June 2022, on by default but disable-able; **phase 3: March 14,
2023, on by default with no way to disable**) ([Microsoft — KB5004442](https://support.microsoft.com/topic/kb5004442-manage-changes-for-windows-dcom-server-security-feature-bypass-cve-2021-26414-f1400b52-c141-43d2-941e-37ed901c769c)).
During phases 1–2 you could control it with
`HKLM\SOFTWARE\Microsoft\Ole\AppCompat\RequireIntegrityActivationAuthenticationLevel` (DWORD).
The same KB defines diagnostic System-log events: **10036** (server-side: activation denied,
"raise the activation authentication level at least to RPC_C_AUTHN_LEVEL_PKT_INTEGRITY"),
**10037/10038** (client-side: which app/PID requested the low level). An **auto-elevation patch**
(since Nov 2022, fully effective with Jan 2023 cumulative updates) raises most *Windows client*
activation requests to PKT_INTEGRITY automatically, which is why most OPC DA clients kept working;
servers that still fail are the reason vendor notices like
[Matrikon's 2022 DCOM security update notice](https://www.matrikonopc.com/downloads/1454/whitepapers/index.aspx)
(archived [copy](https://web.archive.org/web/20240615211950/https://matrikonopc.com/downloads/1454/whitepapers/index.aspx))
exist. If the OPC server requires an insecure activation level, the fix is a vendor update, not
`0x80070005` surgery.

---

## 2. Server-side checklist (DESKTOP-BC2AU7H)

### 2.0 Registry map — every knob at a glance

All machine-wide values live under **`HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Ole`**; per-application
values live under **`HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppID\{AppID-GUID}`**
([Microsoft — AppID Key](https://learn.microsoft.com/en-us/windows/win32/com/appid-key),
[Microsoft — EnableDCOM](https://learn.microsoft.com/en-us/windows/win32/com/enabledcom)).

| Value | Type | Meaning (source) |
|---|---|---|
| `EnableDCOM` | REG_SZ `Y`/`N` | Global DCOM on/off: `N` blocks remote clients launching servers or connecting to objects ([EnableDCOM](https://learn.microsoft.com/en-us/windows/win32/com/enabledcom)) |
| `MachineLaunchRestriction` | REG_BINARY (SD) | Computer-wide minimum for **launch/activate**; "an additional AccessCheck … on each … activation, or launch" ([MachineLaunchRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machinelaunchrestriction), [SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)) |
| `MachineAccessRestriction` | REG_BINARY (SD) | Computer-wide minimum for **calls**; same additional AccessCheck ([MachineAccessRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machineaccessrestriction), [SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)) |
| `DefaultLaunchPermission` | REG_BINARY (ACL) | Launch ACL for classes with no per-AppID `LaunchPermission` ([DefaultLaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultlaunchpermission)) |
| `DefaultAccessPermission` | REG_BINARY (ACL) | Access ACL for classes with no per-AppID `AccessPermission` and no `CoInitializeSecurity` ([DefaultAccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultaccesspermission)) |
| `LegacyAuthenticationLevel` | REG_WORD | Default authn level for apps that don't call `CoInitializeSecurity`; absent ⇒ 2 = CONNECT ([LegacyAuthenticationLevel](https://learn.microsoft.com/en-us/windows/win32/com/legacyauthenticationlevel), [COM Security Defaults](https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults)) |
| `LegacyImpersonationLevel` | REG_WORD | Default impersonation level; absent ⇒ identify ([COM Security Defaults](https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults)) |
| `AppCompat\RequireIntegrityActivationAuthenticationLevel` | REG_DWORD | DCOM-hardening gate (see §1.4) ([KB5004442](https://support.microsoft.com/topic/kb5004442-manage-changes-for-windows-dcom-server-security-feature-bypass-cve-2021-26414-f1400b52-c141-43d2-941e-37ed901c769c)) |

Per-AppID values ([AppID Key](https://learn.microsoft.com/en-us/windows/win32/com/appid-key)):
`LaunchPermission`, `AccessPermission`, `AuthenticationLevel`, `RunAs`, `LocalService`,
`DllSurrogate`, `Endpoints` (pin a fixed TCP port per application), `RemoteServerName`.

The access rights inside these ACLs (SP2+ format) are
([SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)):

```
COM_RIGHTS_EXECUTE          1   (must always be present; its absence makes the SD invalid)
COM_RIGHTS_EXECUTE_LOCAL    2
COM_RIGHTS_EXECUTE_REMOTE   4
COM_RIGHTS_ACTIVATE_LOCAL   8
COM_RIGHTS_ACTIVATE_REMOTE  16
```

### 2.1 Enable DCOM and set the machine-wide authentication level

- Registry: `HKLM\SOFTWARE\Microsoft\Ole\EnableDCOM = "Y"` (REG_SZ). `N` means: "No remote clients may
  launch servers or connect to objects on this computer … all DCOM traffic is blocked"
  ([EnableDCOM](https://learn.microsoft.com/en-us/windows/win32/com/enabledcom)).
- GUI equivalent: `dcomcnfg` → Component Services → Computers → **My Computer** → Properties →
  **Default Properties** tab → *Enable Distributed COM on this computer*
  ([Microsoft — Setting System-Wide Security Using DCOMCNFG](https://learn.microsoft.com/en-us/windows/win32/com/setting-machine-wide-security-using-dcomcnfg)).
- **Default Authentication Level** must not be **(None)**; choose **Connect** (the system default —
  "at the first call a client makes to the server, COM does an authentication check"
  ([COM Security Defaults](https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults),
  [DCOMCNFG system-wide](https://learn.microsoft.com/en-us/windows/win32/com/setting-machine-wide-security-using-dcomcnfg)).
  Authentication levels: 1=None, 2=Connect, 3=Call, 4=Pkt, 5=Pkt Integrity, 6=Pkt Privacy
  ([Authentication Level Constants](https://learn.microsoft.com/en-us/windows/win32/com/com-authentication-level-constants)).
  Matrikon's guide sets the OPC server AppID to **Connect** for OPC DA
  ([Matrikon — Establishing OPC Communication on XP SP2 / 2003 SP1](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf),
  archived [copy](https://web.archive.org/web/20250528090916/https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
- **Default Impersonation Level**: **Identify** is the COM default ([COM Security Defaults](https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults));
  the server-side value matters less than the client's, but **Impersonate** is what the OPC server
  needs in order to act as the client for ACL checks ([Impersonation Levels](https://learn.microsoft.com/en-us/windows/win32/com/impersonation-levels)).

### 2.2 Machine-wide launch/access restrictions (the SP2+ "limits")

These are the *ceilings*: anything denied here is denied everywhere, even if an AppID grants it
([MachineLaunchRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machinelaunchrestriction),
[MachineAccessRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machineaccessrestriction),
[SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).
They are stored as binary security descriptors at:

```
HKLM\SOFTWARE\Microsoft\Ole\MachineLaunchRestriction   (REG_BINARY, SECURITY_DESCRIPTOR)
HKLM\SOFTWARE\Microsoft\Ole\MachineAccessRestriction    (REG_BINARY, SECURITY_DESCRIPTOR)
```

Defaults that matter for a workgroup (XP SP2 / 2003 SP1 behavior, unchanged on modern Windows):

| Restriction | Default grants | Consequence |
|---|---|---|
| `MachineLaunchRestriction` | Administrators: LL+LA+RL+RA; Everyone: LL+LA only (XP SP2); Server 2003 SP1 additionally gives **Distributed COM Users** (S-1-5-32-562) LL+LA+RL+RA | **Non-admin users have NO remote launch/activate by default** — the single most common cause of "admin works, standard user gets 0x80070005" |
| `MachineAccessRestriction` | Everyone: LC+RC; Anonymous: LC (XP SP2) / LC+RC (2003 SP1) | Remote *calls* by authenticated users are allowed by default |

(Grant tables: [SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1);
LL=Local Launch, LA=Local Activation, RL=Remote Launch, RA=Remote Activation, LC=Local Call, RC=Remote Call.)

Fix: add the OPC user (or the **Distributed COM Users** group, `S-1-5-32-562`
([Well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids)))
with **Remote Launch + Remote Activation** to `MachineLaunchRestriction` — either via
`dcomcnfg` → My Computer → **COM Security** tab → *Launch and Activation Permissions* → **Edit Limits**,
or the Local Security Policy items **"DCOM: Machine Access Restrictions in Security Descriptor
Definition Language (SDDL) Syntax"** / **"DCOM: Machine Launch Restrictions …"** — "Existence of this
policy overrides values in MachineAccessRestriction / MachineLaunchRestriction"
([SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).
Matrikon's guide does exactly this via `secpol.msc` → Security Options → edit both DCOM restriction
policies and add Everyone / Interactive / Network / System
([Matrikon — XP SP2/2003 SP1 DCOM guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).

Only Administrators can modify these settings ([SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).

### 2.3 Defaults used when an AppID sets nothing (`DefaultLaunchPermission` / `DefaultAccessPermission`)

- `DefaultLaunchPermission`: default ACL is **Administrators: allow launch; SYSTEM: allow launch;
  INTERACTIVE: allow launch** ([DefaultLaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultlaunchpermission)).
  Critical subtlety: the `INTERACTIVE` SID (S-1-5-4) only covers *console-interactive* logons;
  a remote DCOM client logs on across the network and carries the **NETWORK** SID (S-1-5-2)
  ([Well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids)) —
  so relying on these defaults *excludes every remote client*. You must explicitly grant the remote
  user/group.
- `DefaultAccessPermission`: "**By default, this value has no entries in it. Only the server
  principal and system are allowed to call the server.**" ([DefaultAccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultaccesspermission),
  [COM Security Defaults](https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults)).
  If the OPC server does not call `CoInitializeSecurity` and has no per-AppID `AccessPermission`,
  remote users get `0x80070005` at connect time unless you populate this ACL.
- GUI: `dcomcnfg` → My Computer → **COM Security** tab → *Access Permissions* / *Launch and
  Activation Permissions* → **Edit Default** (defaults) vs. **Edit Limits** (machine-wide ceilings)
  ([DCOMCNFG system-wide](https://learn.microsoft.com/en-us/windows/win32/com/setting-machine-wide-security-using-dcomcnfg),
  [SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).

### 2.4 Per-application (per-AppID) settings

Find the OPC server's AppID from its CLSID: `HKLM\SOFTWARE\Classes\CLSID\{…}\AppID` →
`HKLM\SOFTWARE\Classes\AppID\{AppID-GUID}`. AppIDs "group the configuration options for one or
more DCOM objects into one centralized location in the registry"
([AppID Key](https://learn.microsoft.com/en-us/windows/win32/com/appid-key)).

- `LaunchPermission` (REG_BINARY ACL) — "the ACL of the principals that can start new servers for
  this class … checked while impersonating the client"; falls back to `DefaultLaunchPermission`
  ([LaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/launchpermission)).
- `AccessPermission` (REG_BINARY ACL) — who can call instances; used only by apps that do **not**
  call `CoInitializeSecurity`; falls back to `DefaultAccessPermission`
  ([AccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/accesspermission)).
- `AuthenticationLevel` (REG_WORD) — per-app default authn level for apps that don't call
  `CoInitializeSecurity` ([AppID Key](https://learn.microsoft.com/en-us/windows/win32/com/appid-key)).
- `RunAs` / `LocalService` — identity, see §2.5.
- `Endpoints` — pin the DCOM endpoint to a fixed TCP port (alternative to opening the whole
  dynamic range): "Configures a COM application to use a specified TCP port number for DCOM
  communications" ([AppID Key](https://learn.microsoft.com/en-us/windows/win32/com/appid-key)).

### 2.5 dcomcnfg walkthrough for the OPC server AppID

The canonical vendor procedure (steps consolidated from the Matrikon and Kepware guides):

1. `dcomcnfg` → Component Services → Computers → My Computer → **DCOM Config**; locate the OPC
   server (e.g. "OPC Server") **and OPCEnum** ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf),
   [Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
2. **General** tab → Authentication Level = **Default** (Kepware) or **Connect** (Matrikon) —
   never None ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf),
   [Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
3. **Location** tab → only **Run application on this computer** ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
4. **Security** tab → **Launch and Activation Permissions** → **Customize** → Edit → add the OPC
   user/group; tick **Local Launch / Remote Launch / Local Activation / Remote Activation** as
   required ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf),
   [Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
5. Same tab → **Access Permissions** → **Customize** → Edit → add the same user/group with
   **Remote Access** (and Local Access if the server is also used locally)
   ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
6. Repeat for **OPCEnum** (the OPC Foundation component that browses the registry for OPC servers;
   it "runs as a System service and provides a means to browse the local machine for OPC servers
   and then expose the list to the OPC client")
   ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf));
   Matrikon sets OPCEnum to Authentication Level **Connect**, custom launch+access permissions, and
   Identity = **The system account** ([Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
7. **Identity** tab → see §2.6.
8. Changes may require a restart of the OPC server runtime or the machine
   ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).

### 2.6 Identity tab — interactive user vs. launching user vs. this user vs. service

The Identity tab decides **which security context the server process runs in**, which drives both
who can be impersonated and what resources the server can reach ([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)):

- **The interactive user** — `RunAs = "Interactive User"`: "the server is run in the identity of the
  user currently logged on and is connected to the interactive desktop"
  ([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)). If nobody is logged on at the
  console, activation fails — a common VM trap. It also ties every client to one console session.
- **The launching user** — the server runs under *the identity that activated it* (the SCM launches
  the process while impersonating the caller — launch ACLs "are checked while impersonating the
  client" ([LaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/launchpermission))).
  Implication: each distinct user gets their own server instance; and if a second user connects to
  an instance already running as someone else, the *access* ACL on the running instance (not launch)
  decides. For multi-user workgroups this produces unpredictable instances.
- **This user** — `RunAs = "Domain\User"`: the server runs in a dedicated logon session. "The
  user-name and password are then used to create a logon session in which the server is run … the
  user runs with its own desktop and window station." The account **must have the right to log on
  as a batch job** (secpol → Local Policies → User Rights Assignment → *Log on as a batch job*)
  ([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)). This is the predictable choice
  for a server that must be reachable with nobody logged on.
- **The system account / Local Service / Network Service** — for COM servers that are Windows
  services (`LocalService` AppID value; `RunAs` accepts `nt authority\localservice`,
  `nt authority\networkservice`, `nt authority\system`)
  ([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas),
  [AppID Key](https://learn.microsoft.com/en-us/windows/win32/com/appid-key)). Note the LOCAL SERVICE
  caveat: it "presents anonymous credentials on the network"
  ([Microsoft — Local accounts](https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts)) —
  relevant if the OPC server itself needs to call out to another machine.

Vendor guidance: Matrikon — "Ensure that your server is either running as 'The interactive user' OR,
if it is running as a service, 'The system account'" ([Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
Kepware — "When remote OPC connections are required, selecting **System Service Mode** produces the
most predictable results. The Runtime is started when the system starts and does not require user
intervention. A specific user is not required to be logged on"; when the process mode is Interactive,
set Identity to **This user** (the specified user need not be logged on; for KEPServerEX it must be
an Administrator) ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).

### 2.7 Anonymous Logon handling

- The anonymous logon SID is **S-1-5-7** ("Anonymous logon, or null session logon")
  ([Well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids)).
- After XP SP2/2003 SP1, **Anonymous has no launch/activate rights at all** and only local (XP SP2)
  or local+remote (2003 SP1) access rights ([SP2/2003SP1 tables](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).
- **By default the token created for anonymous connections does not include the Everyone SID**, so
  "permissions that are assigned to the Everyone group don't apply to anonymous users". The policy
  *Network access: Let Everyone permissions apply to anonymous users* (disabled by default) adds it
  ([Microsoft — Let Everyone permissions apply to anonymous users](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-let-everyone-permissions-apply-to-anonymous-users)).
- Why OPC guides still touch ANONYMOUS LOGON: OPCEnum is browsed before/independent of user
  authentication in some client implementations. Kepware: add **ANONYMOUS LOGON** with local+remote
  permissions under COM Security → Access Permissions → **Edit Limits** — "OPCEnum overrides DCOM
  settings and opens accessibility to everyone. In Windows XP Service Pack 2 and above, this step is
  required because applications are not permitted to perform this action without user interaction"
  ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
  Matrikon grants Anonymous Logon + Everyone + Interactive + Network + System in all four COM
  Security lists (limits and defaults, access and launch) ([Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)).
  If you authenticate the OPC session properly (§3), anonymous grants are only needed for the
  browse step — treat them as a scope-limited exception.

### 2.8 Firewall — TCP 135 + dynamic RPC ports, in BOTH directions

- DCOM needs the **RPC Endpoint Mapper on TCP 135** plus the **dynamic ports** the server object
  registers. Default dynamic range on Windows 10: **49152–65535** (Vista+; XP/2003 used
  1025–5000) ([Service overview and network port requirements, KB 832017](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/service-overview-and-network-port-requirements)).
- Inbound on the **server** (DESKTOP-BC2AU7H): TCP 135 + 49152–65535 (or the pinned port, §2.4).
  Inbound on the **client** (DESKTOP-3QEUJIV): the same, **for callbacks** — "the firewall must
  also be set on client computer so that callbacks can be received"
  ([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
- Vendor pattern: allow the specific executables (OPCEnum.exe + the OPC server .exe) **and** add
  TCP 135; Kepware also lists both sides explicitly ([Kepware guide §5](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf));
  Matrikon's XP-era guide simply disables the firewall for testing ([Matrikon guide](https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)) —
  prefer scoped rules on Windows 10.
- Restricting the dynamic range: `HKLM\Software\Microsoft\Rpc\Internet` → `Ports`
  (REG_MULTI_SZ, e.g. `5000-6000`), `PortsInternetAvailable=Y`, `UseInternetPorts=Y`, then restart —
  "All applications that use RPC dynamic port allocation use ports 5000 through 6000, inclusive"
  ([Microsoft — RPC dynamic port allocation with firewalls, KB 154596](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-rpc-dynamic-port-allocation-with-firewalls)).
  Caveats from the same article: keep ≥100 ports, and "you can't use DCOM through firewalls that do
  address translation" (NAT breaks DCOM because raw IPs are marshaled).

### 2.9 Server-side verification commands (reference — run at the console)

```powershell
# DCOM enabled?
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Ole' | Select EnableDCOM, MachineLaunchRestriction, MachineAccessRestriction

# Per-AppID values (find AppID under the CLSID first)
Get-ItemProperty 'HKLM:\SOFTWARE\Classes\AppID\{<AppID-GUID>}' | Select LaunchPermission, AccessPermission, AuthenticationLevel, RunAs

# Effective local-account network model
secedit /export /cfg C:\secpol.cfg /areas USER_RIGHTS   # check SeNetworkLogonRight
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy

# DCOM hardening events (see §1.4): System log, source "DistributedCOM", IDs 10036/10037/10038
Get-WinEvent -LogName System | Where-Object { $_.Id -in 10036,10037,10038 } | Select -First 20
```

---

## 3. Client-side checklist (DESKTOP-3QEUJIV)

### 3.1 Matching local accounts — the workgroup identity contract

In a workgroup there is no domain/KDC, so Kerberos is unavailable and **NTLM** is the protocol:
"NTLM must also be used for logon authentication on stand-alone systems"; NTLM credentials
"consist of a domain name, a user name, and a one-way hash of the user's password" and
authentication is a challenge/response ([Microsoft NTLM](https://learn.microsoft.com/en-us/windows/win32/secauthn/microsoft-ntlm)).
The `Negotiate` package picks Kerberos only when usable, else NTLM
([Microsoft NTLM](https://learn.microsoft.com/en-us/windows/win32/secauthn/microsoft-ntlm)).

For NTLM to authenticate a local account against a remote machine, **the same account name and the
same password must exist locally on both machines**: "When working within a workgroup, each user
will need to be created locally on each computer involved in the connection. Furthermore, each
user account must have the same password in order for authentication to occur. A blank password is
not valid in most cases" ([Kepware guide §2.1](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).

Logon type: a DCOM activation is a **network logon** — the token carries the NETWORK group SID
S-1-5-2 ("Users who log on across a network … the corresponding logon type is
LOGON32_LOGON_NETWORK") ([Well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids)).
It is **not** INTERACTIVE (S-1-5-4), which is why the `INTERACTIVE` ACE in `DefaultLaunchPermission`
never covers remote DCOM clients (§2.3).

Related policies that shape the network token:
- *Network access: Sharing and security model for local accounts* — **Classic** authenticates the
  local user as themselves; **Guest only** maps every local-account network logon to Guest.
  Default on Windows is Classic; if something set it to Guest only, SMB may still "work" via Guest
  while DCOM returns `0x80070005` ([Microsoft — Sharing and security model](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-sharing-and-security-model-for-local-accounts),
  [Kepware guide §7.1](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf) —
  "The Sharing and Security Model may need to be set to Classic on the server computer only. An
  error code (HR=80070005) will be returned to the client when attempting to [connect]").
- *Network security: LAN Manager authentication level* — defaults to **Send NTLMv2 responses
  only** on Server 2008 R2+ (registry `HKLM\System\CurrentControlSet\Control\Lsa\LmCompatibilityLevel`)
  ([Microsoft — LAN Manager authentication level](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-security-lan-manager-authentication-level)).
  Both machines should be on NTLMv2; legacy LM levels are a downgrade risk, not a fix.

### 3.2 CoInitializeSecurity — process-wide defaults

`CoInitializeSecurity` "registers security and sets the default security values for the process".
For a **client** the meaningful parameters are `dwAuthnLevel` (default authn level for all proxies)
and `dwImpLevel` (default impersonation level for proxies; "used only when the process is a
client"). If the process never calls it, COM calls it implicitly with registry defaults
(Connect / Identify) ([CoInitializeSecurity](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializesecurity),
[COM Security Defaults](https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults)).
If both client and server call it, the negotiated proxy authn level is "**the higher of the
authentication levels specified by the client and the server**" ([Security Blanket Negotiation](https://learn.microsoft.com/en-us/windows/win32/com/security-blanket-negotiation)).

### 3.3 CoSetProxyBlanket / COAUTHINFO / COAUTHIDENTITY — per-proxy and per-activation identity

- `CoSetProxyBlanket` sets "the authentication information that will be used to make calls on the
  specified proxy" — auth service, authn level, impersonation level, and `pAuthInfo`
  ([CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket)).
- `pAuthInfo` is an `RPC_AUTH_IDENTITY_HANDLE` — for NTLM/Kerberos it points to a
  `SEC_WINNT_AUTH_IDENTITY` (the same layout as `COAUTHIDENTITY`: User / Domain / Password / Flags).
  "If this parameter is **NULL**, DCOM uses the current proxy identity (which is either the process
  token or the impersonation token)." ([CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket),
  [COAUTHIDENTITY](https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-coauthidentity)).
- NTLM constraint: "If NTLMSSP is the authentication service, this value [dwImpLevel] must be
  RPC_C_IMP_LEVEL_IMPERSONATE or RPC_C_IMP_LEVEL_IDENTIFY" ([CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket));
  NTLM delegate-level impersonation works only across threads/processes on the same computer, not
  across machines ([Impersonation Levels](https://learn.microsoft.com/en-us/windows/win32/com/impersonation-levels)).
- **Activation-time identity**: pass a `COAUTHINFO` (which contains a `COAUTHIDENTITY`) via
  `COSERVERINFO.pAuthInfo` to `CoCreateInstanceEx`. "To specify a different client identity for
  computer remote activations. The specified identity will be used for the **launch permission
  check** on the server rather than the real client identity." `dwImpersonationLevel` in
  `COAUTHINFO` "must be RPC_C_IMP_LEVEL_IMPERSONATE or above"
  ([COAUTHINFO](https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-coauthinfo)).
  Method calls made afterwards are **not** covered by `COSERVERINFO` — "this parameter does not
  influence the security settings used when making method calls on the instantiated object. Those
  security settings are configurable, on a per-interface basis, with CoSetProxyBlanket"
  ([CoCreateInstanceEx](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstanceex)).
- For workgroup local accounts, the "domain" in `COAUTHIDENTITY` is the **remote machine name**
  (that is the NTLM authority that holds the SAM database).

### 3.4 Cloaking — why thread impersonation alone does NOT change the RPC identity

This is the #1 client-side surprise. The proxy's presented identity is a function of cloaking flag,
process token, thread token, and whether a proxy identity was set ([Cloaking](https://learn.microsoft.com/en-us/windows/win32/com/cloaking)):

| Cloaking flags | Thread token | Proxy identity previously set | Identity the server sees |
|---|---|---|---|
| Cloaking not set | don't care | don't care | **Process token or authentication identity** |
| EOAC_STATIC_CLOAKING | present | no | Thread token (fixed at first call) |
| EOAC_STATIC_CLOAKING | present | yes | Current proxy identity |
| EOAC_DYNAMIC_CLOAKING | present | don't care | **Thread token (per call)** |
| EOAC_DYNAMIC_CLOAKING | absent | don't care | Process token |

Consequences:
- A client that calls `LogonUser` + `ImpersonateLoggedOnUser` (or `RunImpersonated`) and then makes
  a DCOM call **without setting EOAC_DYNAMIC_CLOAKING still presents the process token** — the
  server performs its ACL checks against the wrong identity → `0x80070005`. "When impersonation is
  used without cloaking, the identity presented to a downstream server is that of the immediate
  calling process" ([Cloaking](https://learn.microsoft.com/en-us/windows/win32/com/cloaking)).
- The same rule applies to **activation**: with no cloaking, the SCM checks the process token
  ([Activation Security](https://learn.microsoft.com/en-us/windows/win32/com/activation-security)).
- Cloaking is set via `CoInitializeSecurity` `dwCapabilities` or per-proxy via `CoSetProxyBlanket`
  `dwCapabilities` ([Cloaking](https://learn.microsoft.com/en-us/windows/win32/com/cloaking),
  [CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket)).
- **Conflict**: "CoSetProxyBlanket will fail if pAuthInfo is set and one of the cloaking flags is
  set in the dwCapabilities parameter" ([CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket)) —
  per-proxy, choose explicit credentials **or** cloaking, not both.

### 3.5 'Launching user' identity implications for the client

If the server AppID is set to **The launching user**, the server instance runs as *this client's*
account. Practical consequences: (1) the client account must be allowed by the server's launch and
access ACLs **and** by `MachineLaunchRestriction`/`MachineAccessRestriction`; (2) the instance runs
in a session created from a network logon — it has no interactive desktop, so UI-dependent servers
misbehave; (3) different users get different instances, and a second user hitting an already-running
instance is governed by the access ACL on that instance ([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas),
[LaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/launchpermission)).
For a dedicated OPC server VM, prefer a fixed identity (§2.6) and grant the client accounts access.

### 3.6 How this maps to opc-bridge (this repo)

The DA client in `src/OpcBridge.Da/OpcDaClient.cs` activates remote servers via
`Type.GetTypeFromProgID(progId, host)` + `Activator.CreateInstance` (`ConnectDirect` dispatches to
`ConnectRemote`; the legacy `CoCreateInstanceEx`/`ConnectWithImpersonation` paths are gone):

1. **With `RemoteUsername` configured** — `LogonUser(…, LOGON32_LOGON_NEW_CREDENTIALS=9)` +
   `WindowsIdentity.RunImpersonated` wrapping `ConnectRemote` (thread-impersonation path). Per
   §3.4, for the SCM to see the impersonated identity, the COM apartment must have
   **EOAC_DYNAMIC_CLOAKING** configured (activation checks use the thread token only when cloaking
   is set
   ([Activation Security](https://learn.microsoft.com/en-us/windows/win32/com/activation-security)));
   otherwise the server checks the *process* identity.
2. **Without credentials** (host set, `RemoteUsername` empty) — `ConnectRemote` runs directly with
   the process identity (null `COAUTHINFO` → default credentials). A host-only DCOM source is
   valid; you no longer need to supply credentials for the activation to succeed.

Activation failures are classified by `DaConnectErrorClassifier` (`OpcBridge.Da`): a
registered-but-dead server (RPC unavailable, crash on start) or an unreachable host throws
`SourceConnectionLostException`, which the coordinator treats as transient and retries with
backoff; only an explicit "class not registered" (`0x80040154`) or a logon failure stays terminal
(Faulted).

The workgroup requirement from §3.1 applies to both paths: the `RemoteUsername`/`RemotePassword`
must match a local account on DESKTOP-BC2AU7H with an identical password, and the account must be
present in the server's launch + access ACLs (§2.5) and machine-wide restrictions (§2.2).

---

## 4. Troubleshooting the specific symptom

**Symptom.** From DESKTOP-3QEUJIV, a **standard (non-admin) user whose local account exists on
DESKTOP-BC2AU7H with the same name + password** can authenticate over SMB (`IPC$`/`C$` work), but
**COM activation returns `0x80070005 E_ACCESSDENIED`**. An **Administrator account works**.

**What "admin works, standard fails" tells us.** DCOM networking (TCP 135 + dynamic ports) and
NTLM authentication are functioning — otherwise the admin wouldn't work either. The failure is
**authorization**: the standard user's *network token* is being denied by one of the ACL gates in
§1.3. The classic reason a standard user is denied while an admin is allowed is that the ACLs only
contain Administrators (and the machine-wide defaults *are* admin-only for remote launch/activate,
§2.2), or the user's token is being mapped/anonymized.

Run these checks in order (most likely first). Each gives a definitive yes/no.

### Check 1 (most likely) — Machine-wide launch restriction: remote launch/activate for non-admins

**Why.** Default `MachineLaunchRestriction` grants remote launch + remote activation **only to
Administrators** (and, on Server SKUs, Distributed COM Users); Everyone has only *local*
launch/activate ([SP2/2003SP1 tables](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1),
[MachineLaunchRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machinelaunchrestriction)).
The SCM denies the activation before the AppID is even consulted — "principals not given
permissions here cannot obtain them even if the permissions are granted by … [anything else]"
([MachineLaunchRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machinelaunchrestriction)).

**Test (on DESKTOP-BC2AU7H).** `dcomcnfg` → My Computer → Properties → **COM Security** →
*Launch and Activation Permissions* → **Edit Limits** (or `secpol.msc` → Security Options →
*DCOM: Machine Launch Restrictions …*) and confirm the standard user (or a group it belongs to)
has **Remote Launch + Remote Activation**.

**Fix.** Add the user — or better, add the user to the built-in **Distributed COM Users** group
(`S-1-5-32-562` ([Well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids)))
and grant that group Remote Launch + Remote Activation in the limits. This is the canonical
remediation for "standard user denied, admin OK"
([SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)).

### Check 2 — Per-AppID LaunchPermission is missing the user (server AppID + OPCEnum)

**Why.** The SCM checks the class's `LaunchPermission` ACL "while impersonating the client"; the
standard user must be present with execute (and, in SP2+ format, remote) rights
([LaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/launchpermission)).
If the AppID relies on `DefaultLaunchPermission`, remember its ACEs are Administrators / SYSTEM /
**INTERACTIVE** — none of which matches a NETWORK logon token (§2.3)
([DefaultLaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultlaunchpermission)).

**Test.** `dcomcnfg` → DCOM Config → OPC server → Security → Launch and Activation Permissions →
Customize → Edit; repeat for **OPCEnum** (used for browsing)
([Kepware guide](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).

**Fix.** Add the user or group with **Remote Launch + Remote Activation** on both AppIDs (§2.5).

### Check 3 — AccessPermission / DefaultAccessPermission on the running server

**Why.** If the server is already running (e.g., started by an admin session or as a service),
activation may succeed but the connect/call is denied by the access gate. `DefaultAccessPermission`
is **empty by default** — "only the server principal and system are allowed to call the server"
([DefaultAccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultaccesspermission)).
The running server checks the ACL "while impersonating the caller"
([AccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/accesspermission)).

**Test/Fix.** Per-AppID Access Permissions → Customize → add the user with **Remote Access**;
if the server calls `CoInitializeSecurity`, the ACL there wins over the registry
([Setting Process-Wide Security](https://learn.microsoft.com/en-us/windows/win32/com/setting-processwide-security),
[CoInitializeSecurity](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializesecurity)) —
check the vendor docs for how that server exposes it. Also confirm `MachineAccessRestriction`
allows remote calls for the user (default: Everyone LC+RC, so usually fine)
([MachineAccessRestriction](https://learn.microsoft.com/en-us/windows/win32/com/machineaccessrestriction)).

### Check 4 — 'Everyone' isn't covering the network token (Guest mapping / anonymous)

**Why.** Two distinct mechanisms remove the Everyone/authenticated identity from the token:
- **"Guest only" sharing model**: "network logons that use local accounts are automatically mapped
  to the Guest account" — the ACLs then check Guest, not the user, and the user appears as
  anonymous/guest → `0x80070005` even though SMB "worked" (as Guest)
  ([Sharing and security model](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-sharing-and-security-model-for-local-accounts),
  [Kepware guide §7.1](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
- **Anonymous token lacks Everyone**: "By default, the token that is created for anonymous
  connections doesn't include the Everyone SID", so Everyone-based grants don't apply
  ([Let Everyone permissions apply to anonymous users](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-let-everyone-permissions-apply-to-anonymous-users)).
- **UAC filtering of admin tokens** (the *reverse* failure mode — admin account fails while a
  standard user works): a local SAM admin logging on across the network "is issued a standard user
  token with no administrative rights, but without the ability to request or receive elevation" —
  the token is filtered, so ACEs that grant the *Administrators* group don't match and
  administrative shares are inaccessible
  ([Local accounts](https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts),
  [UAC and remote restrictions](https://learn.microsoft.com/en-us/troubleshoot/windows-server/windows-security/user-account-control-and-remote-restriction)).
  To let remote SAM admins keep full (elevated) tokens, set
  `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\LocalAccountTokenFilterPolicy = 1`
  (DWORD) — default 0 builds the filtered token
  ([UAC and remote restrictions](https://learn.microsoft.com/en-us/troubleshoot/windows-server/windows-security/user-account-control-and-remote-restriction)).
  For DCOM ACLs, prefer granting the **Distributed COM Users** group rather than Administrators so
  membership doesn't depend on token elevation.

**Fix (server).** `secpol.msc` → Security Options → *Network access: Sharing and security model
for local accounts* = **Classic – local users authenticate as themselves** (default; restore it if
changed). **Fix (client, only if OPCEnum browsing is the failure).** Enable *Network access: Let
Everyone permissions apply to anonymous users* on the client — Kepware says this is a
client-side-only setting for browse failures ([Kepware guide §7.2](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).

### Check 5 — Server Identity = "The interactive user" with no logged-on console session

**Why.** "If the user name is 'Interactive User', the server is run in the identity of the user
currently logged on and is connected to the interactive desktop"
([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)). A headless VM with nobody at
the console cannot start the server; depending on the failure path this surfaces as access denied
or as a server-start error.

**Test/Fix.** dcomcnfg → server AppID → **Identity** tab. For a VM, set **The system account** (if
it is a service) or **This user** with an account that has *Log on as a batch job*
([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)); at minimum ensure an interactive
session exists when using *The interactive user*.

### Check 6 — Service-hosted OPC server (LocalService / service account) ACLs

**Why.** Many OPC servers (KEPServerEX Runtime, OPCEnum) run as Windows services — "selecting
System Service Mode produces the most predictable results"
([Kepware guide §3.2](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
If the server's DCOM identity is the service account while its AppID launch/access ACLs reference a
user, or the service runs as LOCAL SERVICE ("presents anonymous credentials on the network"
([Local accounts](https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts))),
remote callers can be denied. Also verify the service is actually **running** — a stopped service
makes activation fail.

**Test/Fix.** `services.msc` (service state and *Log on as*), dcomcnfg Identity tab for the same
AppID, and the access ACLs from Check 3.

### Check 7 — Client-side identity: cloaking / impersonation / COAUTHIDENTITY

**Why.** If the client activates while impersonating a thread token **without cloaking**, the SCM
sees the process token and performs launch checks against the wrong identity (§3.4)
([Activation Security](https://learn.microsoft.com/en-us/windows/win32/com/activation-security),
[Cloaking](https://learn.microsoft.com/en-us/windows/win32/com/cloaking)). Or the credentials passed
via `COAUTHINFO`/`COAUTHIDENTITY` name a different account than the one granted on the server
([COAUTHINFO](https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-coauthinfo)).
NTLM also requires the proxy impersonation level to be IDENTIFY or IMPERSONATE
([CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket)).

**Test.** Check what identity the server actually recorded: on DESKTOP-BC2AU7H, `eventvwr` → System →
DistributedCOM events show the failing user; or temporarily add the *process* account (the identity
running the bridge, e.g. the service account) to the server ACLs — if that makes it work, it's a
cloaking/identity mismatch on the client. See §3.6 for the two opc-bridge activation paths and what
each requires.

**Fix.** Either pass the remote credentials explicitly (`COAUTHINFO`/`COAUTHIDENTITY` at activation,
`CoSetProxyBlanket` with `pAuthInfo` for method calls) **or** configure `EOAC_DYNAMIC_CLOAKING`
(process-wide or per-proxy) so impersonated thread tokens are honored — not both on the same proxy
([CoSetProxyBlanket](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket)).

### Check 8 — 'Access this computer from the network' (SeNetworkLogonRight)

**Why.** DCOM/COM+ network connections need this user right: "This capability is required by many
network protocols, including SMB …, and Component Object Model Plus (COM+)"
([Access this computer from the network](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/access-this-computer-from-the-network)).
Default membership includes Everyone/Administrators/Users; if the standard user was removed, its
network logons (SMB included) would fail — so **SMB working makes this unlikely**, but verify it
when SMB was accessed via a different account.

**Test/Fix.** `secpol.msc` → Local Policies → User Rights Assignment → *Access this computer from
the network*; add the user or a containing group.

### Check 9 — SAM name vs. Microsoft-account display name

**Why.** A local account created from a Microsoft account gets a **generated SAM user name** that
differs from the display name. NTLM authenticates by SAM name + password, so a client using the
display name (or a name that differs between the two machines) fails to authenticate and falls back
to anonymous/guest → `0x80070005` — while the same *display* identity may look "present" on both
machines. Confirm the actual SAM name and password match on both machines
([Local accounts](https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts),
[Kepware guide §2.1](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)).
Also disable *User must change password at next logon* and avoid blank passwords ("A blank password
is not valid in most cases" for workgroup DCOM
([Kepware guide §2.1](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf))).

**Test.** On both machines: `whoami /user` (SAM name + SID); ensure identical SAM name, password,
and that the account is not disabled. Prefer a purpose-built local account (e.g. `opcdcom`) created
identically on both machines.

### Check 10 — DCOM hardening blocking legacy activation (post-March-2023)

**Why.** Activation with an authentication level below `RPC_C_AUTHN_LEVEL_PKT_INTEGRITY` is denied
by default since the March 2023 phase (cannot be disabled), with System-log events 10036/10037/10038
([KB5004442](https://support.microsoft.com/topic/kb5004442-manage-changes-for-windows-dcom-server-security-feature-bypass-cve-2021-26414-f1400b52-c141-43d2-941e-37ed901c769c)).

**Test.** `eventvwr` → System → source *DistributedCOM*; look for **10036** on the server ("The
server-side authentication level policy does not allow the user … to activate DCOM server") and
**10037/10038** on the client ("requesting to activate CLSID … with [default] authentication level
at …").

**Fix.** Not an ACL problem: the client must request PKT_INTEGRITY+ (the Windows auto-elevation
patch does this for most clients since Jan 2023) or the vendor must ship a compliant OPC server
([KB5004442](https://support.microsoft.com/topic/kb5004442-manage-changes-for-windows-dcom-server-security-feature-bypass-cve-2021-26414-f1400b52-c141-43d2-941e-37ed901c769c)).

### Ranked summary

| Rank | Check | Key evidence | Fix on |
|---|---|---|---|
| 1 | MachineLaunchRestriction remote launch/activate | admin-only by default ([SP2/2003SP1](https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1)) | server |
| 2 | Per-AppID LaunchPermission (server + OPCEnum) | user missing, or INTERACTIVE-only default ([LaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/launchpermission), [DefaultLaunchPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultlaunchpermission)) | server |
| 3 | AccessPermission / DefaultAccessPermission | default ACL is empty ([DefaultAccessPermission](https://learn.microsoft.com/en-us/windows/win32/com/defaultaccesspermission)) | server |
| 4 | Guest mapping / anonymous Everyone-SID | token doesn't carry the user ([Sharing model](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-sharing-and-security-model-for-local-accounts)) | server (client for browse) |
| 5 | Identity = interactive user, no session | nobody logged on ([RunAs](https://learn.microsoft.com/en-us/windows/win32/com/runas)) | server |
| 6 | Service identity / service stopped | service-hosted ACLs ([Kepware §3.2](https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)) | server |
| 7 | Client cloaking / COAUTHIDENTITY | wrong identity presented ([Activation Security](https://learn.microsoft.com/en-us/windows/win32/com/activation-security), [Cloaking](https://learn.microsoft.com/en-us/windows/win32/com/cloaking)) | client |
| 8 | SeNetworkLogonRight | user right removed ([Access this computer](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/access-this-computer-from-the-network)) | server |
| 9 | SAM name / MS-account mismatch | display ≠ SAM name ([Local accounts](https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts)) | both |
| 10 | DCOM hardening events 10036–10038 | authn level < PKT_INTEGRITY ([KB5004442](https://support.microsoft.com/topic/kb5004442-manage-changes-for-windows-dcom-server-security-feature-bypass-cve-2021-26414-f1400b52-c141-43d2-941e-37ed901c769c)) | client/vendor |

---

## 5. Sources

All primary sources cited inline above. Archived links are provided where a vendor page has moved.

### Microsoft Learn — COM/DCOM security

1. Security in COM — https://learn.microsoft.com/en-us/windows/win32/com/security-in-com
2. Activation Security — https://learn.microsoft.com/en-us/windows/win32/com/activation-security
3. COM Security Defaults — https://learn.microsoft.com/en-us/windows/win32/com/com-security-defaults
4. Enabling COM Security Using DCOMCNFG — https://learn.microsoft.com/en-us/windows/win32/com/enabling-com-security-using-dcomcnfg
5. Setting System-Wide Security Using DCOMCNFG — https://learn.microsoft.com/en-us/windows/win32/com/setting-machine-wide-security-using-dcomcnfg
6. Setting Process-Wide Security — https://learn.microsoft.com/en-us/windows/win32/com/setting-processwide-security
7. Security Blanket Negotiation — https://learn.microsoft.com/en-us/windows/win32/com/security-blanket-negotiation
8. Authentication Level Constants — https://learn.microsoft.com/en-us/windows/win32/com/com-authentication-level-constants
9. Impersonation Levels — https://learn.microsoft.com/en-us/windows/win32/com/impersonation-levels
10. Cloaking — https://learn.microsoft.com/en-us/windows/win32/com/cloaking
11. Instance Creation Helper Functions — https://learn.microsoft.com/en-us/windows/win32/com/instance-creation-helper-functions
12. DCOM Security Enhancements in Windows XP SP2 / 2003 SP1 — https://learn.microsoft.com/en-us/windows/win32/com/dcom-security-enhancements-in-windows-xp-service-pack-2-and-windows-server-2003-service-pack-1
13. AppID Key — https://learn.microsoft.com/en-us/windows/win32/com/appid-key
14. LaunchPermission — https://learn.microsoft.com/en-us/windows/win32/com/launchpermission
15. AccessPermission — https://learn.microsoft.com/en-us/windows/win32/com/accesspermission
16. DefaultLaunchPermission — https://learn.microsoft.com/en-us/windows/win32/com/defaultlaunchpermission
17. DefaultAccessPermission — https://learn.microsoft.com/en-us/windows/win32/com/defaultaccesspermission
18. MachineLaunchRestriction — https://learn.microsoft.com/en-us/windows/win32/com/machinelaunchrestriction
19. MachineAccessRestriction — https://learn.microsoft.com/en-us/windows/win32/com/machineaccessrestriction
20. EnableDCOM — https://learn.microsoft.com/en-us/windows/win32/com/enabledcom
21. LegacyAuthenticationLevel — https://learn.microsoft.com/en-us/windows/win32/com/legacyauthenticationlevel
22. RunAs — https://learn.microsoft.com/en-us/windows/win32/com/runas
23. CoCreateInstanceEx — https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cocreateinstanceex
24. CoInitializeSecurity — https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializesecurity
25. CoSetProxyBlanket — https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cosetproxyblanket
26. COAUTHINFO — https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-coauthinfo
27. COAUTHIDENTITY — https://learn.microsoft.com/en-us/windows/win32/api/wtypesbase/ns-wtypesbase-coauthidentity

### Microsoft Learn — identity, authentication, networking

28. Microsoft NTLM — https://learn.microsoft.com/en-us/windows/win32/secauthn/microsoft-ntlm
29. Well-known SIDs — https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids
30. Local accounts — https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts
31. User Account Control and remote restrictions (LocalAccountTokenFilterPolicy) — https://learn.microsoft.com/en-us/troubleshoot/windows-server/windows-security/user-account-control-and-remote-restriction
32. Access this computer from the network (SeNetworkLogonRight) — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/access-this-computer-from-the-network
33. Network access: Sharing and security model for local accounts — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-sharing-and-security-model-for-local-accounts
34. Network access: Let Everyone permissions apply to anonymous users — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-access-let-everyone-permissions-apply-to-anonymous-users
35. Network security: LAN Manager authentication level — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/network-security-lan-manager-authentication-level
36. Service overview and network port requirements (KB 832017) — https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/service-overview-and-network-port-requirements
37. RPC dynamic port allocation with firewalls (KB 154596) — https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/configure-rpc-dynamic-port-allocation-with-firewalls
38. KB5004442 — Manage changes for Windows DCOM Server Security Feature Bypass (CVE-2021-26414) — https://support.microsoft.com/topic/kb5004442-manage-changes-for-windows-dcom-server-security-feature-bypass-cve-2021-26414-f1400b52-c141-43d2-941e-37ed901c769c

### OPC Foundation

39. OPC Classic (COM/DCOM basis of OPC DA) — https://opcfoundation.org/about/opc-technologies/opc-classic/
40. OPC Classic Data Access specifications (incl. DA 2.05a) — https://opcfoundation.org/developer-tools/specifications-classic/data-access/

### Vendor DCOM guides

41. Kepware — Remote OPC DA (DCOM) Quick Start Guide, July 2019 — https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf (archived: https://web.archive.org/web/20220120022013/https://www.kepware.com/getattachment/04042e47-c690-467c-a931-a1ca126575db/Remote-OPC-DA-Quick-Start-Guide-DCOM.pdf)
42. Matrikon — Establishing OPC Communication on Windows XP SP2 / 2003 SP1 (DCOM Configuration) — https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf (archived: https://web.archive.org/web/20250528090916/https://www.matrikonopc.com/support/docs/MatrikonOPC-Windows-XPSP2-2003SP1-DCOM-Configuration.pdf)
43. Matrikon — 2022 DCOM Security Update Notice: Impact and Path Forward — https://www.matrikonopc.com/downloads/1454/whitepapers/index.aspx (archived: https://web.archive.org/web/20240615211950/https://matrikonopc.com/downloads/1454/whitepapers/index.aspx)

### In-repo reference

44. `src/OpcBridge.Da/OpcDaClient.cs` — the two remote-activation paths this guide's §3.6 maps onto.
