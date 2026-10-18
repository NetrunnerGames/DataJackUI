# DataJackUI Privacy & Telemetry Policy

We believe in complete transparency regarding network traffic, telemetry, cloud storage, and user privacy. DataJackUI is open-source software; every network call and data-handling mechanism is auditable directly in the repository code.

---

## 1. Executive Summary

* **No Personal Data**: We do not collect names, email addresses, Steam credentials, or personal identifiers.
* **Zero Hardware Fingerprinting**: We do not collect MAC addresses, UUIDs, CPU/GPU serial numbers, or hardware IDs.
* **Zero Local Storage of OAuth Tokens**: Google Drive and OneDrive authentication tokens exist strictly in-memory during identity verification and are **never written to disk or configuration files**.
* **Minimal Anonymous Telemetry**: The application transmits a single anonymous ping on launch solely to count aggregate active installations and app version distribution.
* **Privacy Proxy Shield**: Telemetry pings are proxied through an open Cloudflare Worker to protect user IP addresses from third-party analytics endpoints.

---

## 2. Telemetry & Analytics

### What Is Collected
When DataJackUI starts up, [`AnalyticsService.cs`](src/DataJackUIGui/Services/AnalyticsService.cs) fires an asynchronous, fire-and-forget HTTP POST request to our Cloudflare Worker proxy.

* **Trigger**: Executed once during application startup.
* **Client Payload**:
  ```json
  {
    "event_name": "App Launch",
    "version": "2.13.0"
  }
  ```

### How It Is Processed
The Cloudflare Worker proxy (`https://analytics.netrunnergames.workers.dev/api/launch`) receives the payload and relays it to **PostHog EU Cloud** (`https://eu.i.posthog.com/capture/`):

```json
{
  "api_key": "<server_side_key>",
  "event": "App Launch",
  "distinct_id": "anonymous_desktop_user",
  "properties": {
    "app_version": "2.13.0",
    "project_id": "281781",
    "$lib": "DataJackUI-Desktop"
  }
}
```

* **Static Identifier**: The `distinct_id` is a hardcoded constant string (`"anonymous_desktop_user"`). It is identical across all users and installations worldwide.
* **IP Address Protection**: The Cloudflare Worker functions as a privacy shield between your desktop client and PostHog. Client IP addresses are not logged, retained, or forwarded to PostHog.

### Purpose of Telemetry
This anonymous launch counter exists exclusively to:
1. Estimate daily active user counts to forecast server capacity and bandwidth requirements.
2. Monitor version adoption rates to determine which releases require delta update packages via Velopack.

---

## 3. What We DO NOT Collect

* **No Steam Credentials**: We never collect, transmit, or log Steam usernames, passwords, Steam Guard codes, or Steam Web API keys.
* **No Game Library Tracking**: We never monitor, scan, or report what games you have installed, purchase history, or gameplay sessions.
* **No File System Surveillance**: Outside of the explicit game save directories designated for backups, DataJackUI does not scan or upload user files.
* **No Location Data**: We do not collect GPS, country, or location coordinates.

---

## 4. Cloud Saves & OAuth Security

DataJackUI features a Cloud Saves manager that allows users to back up game save files to local storage or cloud providers (Google Drive, Microsoft OneDrive, Rclone).

### Ephemeral In-Memory Token Handling
To safeguard user security against token compromise or malicious extraction:
* **Zero Disk Persistence**: OAuth 2.0 access tokens and refresh tokens are **never written to disk, settings files (`settings.json`), registry keys, or configuration files (`rclone.conf`)**.
* **Identity Verification Only**: When you authorize Google Drive or OneDrive via the browser (OAuth 2.0 PKCE flow), the ephemeral access token is used strictly in-memory to fetch your display email/username (e.g., `user@gmail.com` or `user@outlook.com`).
* **Immediate Discard**: Once the account label is confirmed, the tokens are immediately released from memory. The application only persists the display name for your UI indicator.

---

## 5. Other Network Interactions

DataJackUI performs network requests strictly to support its advertised features:

1. **Steam Storefront & CDN** (`store.steampowered.com`, `steamcdn-a.akamaihd.net`):
   - Used for public store search queries, game metadata, and header banner images.
2. **GitHub API & Velopack** (`api.github.com`):
   - Checks for software updates every 6 hours and on startup to download delta update packages.
3. **Manifest API Endpoints** (DepotBox, `generator.ryuu.lol`):
   - Queries metadata, manifests, and fixes when explicitly requested by the user.
4. **Cloudflare DNS-over-HTTPS** (`https://1.1.1.1/dns-query`):
   - Optional fallback DNS resolver for environments with restricted or poisoned DNS.

---

## 6. Open Source Verification

The entire source code of DataJackUI is publicly available on GitHub under the MIT License. Anyone can inspect, build, and verify the networking and telemetry behaviors directly:

- **Analytics Service**: [`src/DataJackUIGui/Services/AnalyticsService.cs`](src/DataJackUIGui/Services/AnalyticsService.cs)
- **Cloudflare Worker Proxy**: [`worker/index.js`](worker/index.js)
- **Cloud Saves Service**: [`src/DataJackUIGui/Services/CloudSavesService.cs`](src/DataJackUIGui/Services/CloudSavesService.cs)
- **Settings Storage**: [`src/DataJackUIGui/Services/SettingsService.cs`](src/DataJackUIGui/Services/SettingsService.cs)
