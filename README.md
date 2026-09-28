<div align="center">

<img src="src/DataJackUIGui/icon.ico" width="128" height="128" alt="DataJackUI Logo" />

# DATAJACK UI
### ⚡ NEXT-GEN CYBERPUNK MANIFEST & DEPLOYMENT INTERFACE ⚡

[![Version](https://img.shields.io/badge/VERSION-v2.00.0-06b6d4?style=for-the-badge&logo=github)](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.0)
[![Framework](https://img.shields.io/badge/.NET-8.0_WPF-7000ff?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/LICENSE-MIT-10b981?style=for-the-badge)](LICENSE)
[![Platform](https://img.shields.io/badge/PLATFORM-WINDOWS_10%2F11-0284c7?style=for-the-badge&logo=windows)](https://microsoft.com/windows)

*A high-performance, dark glass desktop client for game manifest deployment, native hooks, and Steam CEF integration.*

[📥 Download Release](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.0) • [🧩 Get Steam Plugin](https://github.com/NetrunnerGames/Jack-in) • [📜 Documentation](#-core-protocols)

---

</div>

## 🌐 OVERVIEW

**DataJackUI** is a modernized, dark-tinted **Ambient Glass** GUI designed for seamless Steam manifest acquisition, Denuvo fix integration, native DLL hook management (`IceBreaker`), and direct CEF store plugin control. Built on **.NET 8 WPF** with hardware-accelerated Windows **Acrylic** transparency, DataJackUI brings a Cyberpunk grid interface to game library management.

---

## ⚡ KEY FEATURES

| Feature | Description |
| :--- | :--- |
| 🪟 **Netrunner Ambient Glass** | Dark-tinted Windows 10/11 **Acrylic** glass backdrop (`#090a0f` deep dark cyan tint with 12px blur). |
| 🎯 **Real-Time Store Strips** | Instant game-only Top Sellers and Popular New Releases fetched directly from Steam's storefront query engine. |
| 🔌 **Jack-in CEF Plugin** | Embedded Steam store page injection allowing 1-click manifest adding straight from the Steam desktop app. |
| 🧊 **IceBreaker Hook Suite** | Native `version.dll` hook manager and CloudRedirect integration for seamless manifest injection. |
| 🛠️ **Denuvo Fixes Matrix** | Integrated fix repository and game repair routines with automated backups. |
| 🌐 **Cloudflare DoH Guard** | Built-in **DNS over HTTPS** resolution (`https://1.1.1.1/dns-query`) to bypass ISP domain blocks & throttling. |

---

## 🚀 QUICK START

### 1. Standalone Installer / Portable Build
1. Download **`DataJackUI-win-Setup.exe`** or **`DataJackUI-win-Portable.zip`** from the [Latest Release](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.0).
2. Run the installer or extract the portable folder.
3. Launch `DataJackUI.exe`.

### 2. Steam Client Plugin Integration (Jack-in)
1. Download **`plugin.zip`** from the [Jack-in Repository](https://github.com/NetrunnerGames/Jack-in/releases/tag/v1.0).
2. Extract the `plugin` folder into your Steam desktop client's plugin directory.
3. Restart Steam to access the inline DataJackUI store controls.

---

## ⚙️ BUILDING FROM SOURCE

### Prerequisites
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* Windows 10/11 x64 OS
* PowerShell 7+

### Local Build & Run
```powershell
# Clone repository
git clone https://github.com/NetrunnerGames/DataJackUI.git
cd DataJackUI

# Run framework-dependent local published build
.\run_local.ps1
```

### Packaging 1-Click Release
```powershell
# Build release binaries & Velopack setup packages
.\build_release.ps1 -d 2.00.0 -p 1.0
```

---

## 🖤 CREDITS & ACKNOWLEDGMENTS

DataJackUI is built on top of the open-source foundations and reverse-engineering milestones established by the community:

* **[LuaTools](https://github.com/madoiscool/LuaTools)** — Special thanks to **`madoiscool`** and the LuaTools contributors for the underlying architecture, manifest protocol concepts, and client groundwork.
* **[Velopack](https://github.com/velopack/velopack)** — Next-generation cross-platform installer and auto-update framework.
* **[WPF UI](https://github.com/lepoco/wpfui)** — Modern Fluent UI control library for Windows Presentation Foundation.

---

## ⚖️ LICENSE

Distributed under the **MIT License**. See [`LICENSE`](LICENSE) for complete details.

<div align="center">
  <sub>Engineered by <b>NetrunnerGames</b></sub>
</div>
