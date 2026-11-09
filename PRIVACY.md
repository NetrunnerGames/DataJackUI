# DataJackUI Privacy & Security Policy

We believe in complete transparency regarding network traffic, telemetry, authentication, cloud storage, and user privacy. DataJackUI is open-source software; every network call and data-handling mechanism is auditable directly in the repository code.

## 1. Executive Summary

* **Zero Hardware Fingerprinting**: We do not collect MAC addresses, UUIDs, CPU/GPU serial numbers, or hardware identifiers.
* **No Secret Keystrokes or Password Logging**: We never access, capture, or log Steam passwords, Steam Guard codes, or payment credentials.
* **Encrypted Local Storage (DPAPI + AES-256-GCM)**: Stored user authentication sessions on Windows are secured locally using machine-entropy DPAPI combined with authenticated AES-256-GCM encryption.
* **Minimal Anonymous Telemetry**: The desktop client transmits a single anonymous ping on application startup solely to count aggregate active installations and application version distribution.
* **Privacy Proxy Shield**: Telemetry pings and entitlement checks are proxied through an open Cloudflare Worker to protect client IP addresses from third-party analytics endpoints.

## 2. Telemetry & Analytics

### What Is Collected
When DataJackUI starts up, [`AnalyticsService.cs`](src/DataJackUIGui/Services/AnalyticsService.cs) fires an asynchronous, fire-and-forget HTTP POST request to our Cloudflare Worker proxy.

* **Trigger**: Executed once during application startup.
* **Client Payload**:
  ```json
  {
    "event_name": "App Launch",
    "version": "<app_version>"
  }
  ```

### How It Is Processed
The Cloudflare Worker proxy (`https://analytics.netrunnergames.workers.dev/api/launch`) receives the payload and relays it to an analytics collector:

* **Static Identifier**: The client transmits no user identifier; any downstream aggregation uses a generic static identifier identical across all installations worldwide.
* **IP Address Protection**: The Cloudflare Worker functions as a privacy shield between your desktop client and analytics ingestion. Client IP addresses are not logged, retained, or forwarded.

### Purpose of Telemetry
This anonymous launch counter exists exclusively to:
1. Estimate daily active user counts to forecast server capacity and bandwidth requirements.
2. Monitor version adoption rates to determine which releases require delta update packages via Velopack.

## 3. What We DO NOT Collect

* **No Steam Passwords or Auth Secrets**: We never collect, transmit, or log Steam passwords, Steam Guard codes, or session cookies.
* **No Game Library Surveillance**: We do not monitor, scan, or report what games you have installed, purchase history, or gameplay sessions.
* **No Arbitrary File System Surveillance**: Outside of the explicit game save directories designated for cloud backups and Steam directory configuration, DataJackUI does not scan or upload user files.
* **No Precise Location Tracking**: We do not collect GPS, country, or location coordinates.

## 4. User Accounts, Authentication & Local Storage

DataJackUI supports signing in via **Discord** (using local Discord desktop IPC or OAuth) or **Google** (using OAuth 2.0 PKCE flow) to verify software entitlement and synchronize preferences.

### Session Encryption at Rest
To protect user sessions against unauthorized file extraction or cross-device cloning:
* **Dual-Layer Machine Cryptography**: Stored sessions are encrypted using **AES-256-GCM** with a cryptographic key bound to the local machine, and further wrapped with Windows **Data Protection API (DPAPI)** using machine entropy (`ProtectedData.Protect` under `DataProtectionScope.CurrentUser`).
* **Non-Transferable**: Even if the encrypted credentials file is copied to another machine or user account, it cannot be decrypted.
* **Graceful Revocation**: Signing out immediately clears and deletes the encrypted local session file.

## 5. Cloud Saves & Backup Security

DataJackUI features a Cloud Saves manager that allows users to back up game save files to local storage with automatic pre-restore safety snapshots. Cloud sync integrations with remote storage providers are coming soon.

### Local Snapshot Safety
* **User Control**: Game save backups and restorations are executed solely upon user action or explicitly configured backup schedules. Save data remains entirely inside your own local machine.

## 6. Other Network Interactions

DataJackUI performs network requests strictly to support its advertised features:

1. **Steam Storefront & CDN** (`store.steampowered.com`, `steamcdn-a.akamaihd.net`):
   - Used for public store search queries, game metadata, and header banner images.
2. **GitHub API & Velopack** (`api.github.com`):
   - Checks for software updates periodically and on startup to download delta update packages.
3. **Manifest & API Endpoints** (DepotBox, `generator.ryuu.lol`, Cloudflare Auth Edge):
   - Queries metadata, manifests, and fixes when explicitly requested by the user.
4. **Cloudflare DNS-over-HTTPS** (`https://1.1.1.1/dns-query`):
   - Optional fallback DNS resolver for environments with restricted or poisoned DNS.

## 7. Open Source Verification

The entire source code of DataJackUI is publicly available on GitHub under the MIT License. Anyone can inspect, build, and verify the networking and telemetry behaviors directly:

- **Analytics Service**: [`src/DataJackUIGui/Services/AnalyticsService.cs`](src/DataJackUIGui/Services/AnalyticsService.cs)
- **Auth & Session Service**: [`src/DataJackUIGui/Services/AuthService.cs`](src/DataJackUIGui/Services/AuthService.cs)
- **Cloud Saves Service**: [`src/DataJackUIGui/Services/CloudSavesService.cs`](src/DataJackUIGui/Services/CloudSavesService.cs)
- **Settings Storage**: [`src/DataJackUIGui/Services/SettingsService.cs`](src/DataJackUIGui/Services/SettingsService.cs)
