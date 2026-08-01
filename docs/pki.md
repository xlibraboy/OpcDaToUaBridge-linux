# OPC UA Certificates (PKI)

The bridge uses X.509 certificates to encrypt and authenticate OPC UA communication. This is the **PKI** (Public Key Infrastructure) — a folder of certificates managed automatically.

## Where it lives

```
publish\pki\
  ├── own\          ← the bridge's own certificate (auto-generated on first start)
  ├── trusted\      ← certificates of UA clients you've approved
  ├── issuers\      ← certificate authorities (CAs) that sign client certs
  └── rejected\     ← certificates of UA clients that were rejected
```

## How it works

1. **First start** — the bridge generates a self-signed X.509 certificate and saves it to `pki/own/`. No manual setup needed.
2. **Client connects** — a UA client (e.g. UAExpert) connects to `opc.tcp://host:4840/OpcDaToUaBridge`. The bridge sends its own certificate.
3. **Client trusts the bridge** — the UA client may show a "trust server certificate?" dialog. Accept it.
4. **Bridge trusts the client** — the client's certificate is saved to `pki/trusted/` (if `AutoAcceptUntrustedCertificates` is `true`) or `pki/rejected/` (if `false`).
5. **Encrypted session** — all subsequent UA traffic is encrypted using the negotiated keys.

## AutoAcceptUntrustedCertificates

| Setting | Behavior | Use case |
|---------|----------|----------|
| `true` | Accepts all client certs automatically, saves to `pki/trusted/` | Testing, local network, trusted environment |
| `false` | Rejects unknown client certs → saved to `pki/rejected/` → client cannot connect until you manually move the cert to `pki/trusted/` | Production, untrusted networks |

## Managing certificates

- **Trust a rejected client** — move the `.der` file from `pki/rejected/` to `pki/trusted/` and restart the bridge
- **Regenerate the bridge cert** — delete `pki/own/` and restart; a new certificate is generated. All clients must re-trust the bridge.
- **Never delete `pki/` during updates** — the bridge identity changes if you regenerate, breaking all existing client connections

## Do NOT overwrite `pki/` on update

The `pki/` folder is **user data** — it contains the bridge's identity and trust relationships. Overwriting it during an update:
- Forces the bridge to generate a new certificate
- Breaks all existing UA client connections (they must re-trust)
- Loses the trusted/rejected client list

Always preserve `pki/` across updates. It's listed in the update guide as "never overwrite".
