<div align="center">

<img src="src/DataJackUIGui/icon.ico" width="96" height="96" alt="DataJackUI Icon" />

# DataJackUI

A modern desktop interface for Steam manifest management, native hook injection, and store plugin integration.

<a href="https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.5"><img src="https://img.shields.io/badge/DataJackUI-v2.00.5-090a0f?style=for-the-badge&labelColor=090a0f&logo=github&logoColor=00ffff" height="42" alt="DataJackUI Release" /></a>
<a href="https://github.com/NetrunnerGames/Jack-in/releases/tag/v1.0"><img src="https://img.shields.io/badge/Jack--in_Plugin-v1.0-090a0f?style=for-the-badge&labelColor=090a0f&logo=steam&logoColor=00adf0" height="42" alt="Jack-in Plugin" /></a>
<a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/Framework-.NET_8.0_WPF-090a0f?style=for-the-badge&labelColor=090a0f&logo=dotnet&logoColor=512bd4" height="42" alt="Framework" /></a>
<a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-090a0f?style=for-the-badge&labelColor=090a0f&logo=open-source-initiative&logoColor=3da639" height="42" alt="License" /></a>

</div>

> [!IMPORTANT]
> **DISCLAIMER & NOTICE**
> DataJackUI is an independent open-source client utility. It is not affiliated with, endorsed by, or associated with Valve Corporation or Steam. All game titles, trademarks, and registered assets belong to their respective owners.

## Overview

DataJackUI provides an interface for Steam manifest acquisition, native DLL hook administration, game repair routines, and CEF store plugin control. It includes configurable DNS-over-HTTPS resolution and direct Steam storefront search integration.

## Key Capabilities

* **Dark Acrylic Interface**: Native Windows 10/11 Acrylic backdrop rendering paired with a dark-tinted background container grid.
* **Store Query Engine**: Storefront search queries filtered strictly for real games to populate Top Sellers and Popular New Releases.
* **Jack-in Plugin Backend**: Native RPC integration matching the `Jack-in` Steam CEF store plugin for 1-click manifest addition inside the Steam client.
* **IceBreaker Hook Administration**: Management interface for `version.dll` native hooks and CloudRedirect configurations.
* **Fixes & Diagnostics**: Integrated game fix repository and repair workflows with file backups.
* **Cloudflare DoH Resolution**: Fallback DNS over HTTPS (`https://1.1.1.1/dns-query`) for networks where DNS lookup is restricted or throttled.

## Installation & Downloads

> [!TIP]
> For standard installation, use the standalone installer (`DataJackUI-win-Setup.exe`). Portable builds are also available for zero-installation deployment.

* **Installer**: Download `DataJackUI-win-Setup.exe` from [Releases](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.5).
* **Portable**: Download `DataJackUI-win-Portable.zip` from [Releases](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.5).
* **Steam CEF Plugin**: Download `plugin.zip` from the [Jack-in Repository](https://github.com/NetrunnerGames/Jack-in/releases/tag/v1.0) and extract it into your Steam client plugins directory.

## Building from Source

### Prerequisites
* .NET 8.0 Desktop SDK
* Windows 10 or 11 (x64)

### Local Development Build
```powershell
git clone https://github.com/NetrunnerGames/DataJackUI.git
cd DataJackUI

# Publishes and launches local framework-dependent binary
.\run_local.ps1
```

### Packaging Release Packages
```powershell
# Compiles framework-dependent binaries and generates Velopack installer packages
.\build_release.ps1 -d 2.00.5 -p 1.0
```

## Acknowledgments

DataJackUI is developed using architecture adapted from community open-source projects:

* **[LuaTools](https://github.com/madoiscool/LuaTools)** — Created by **`madoiscool`** and contributors. The manifest handling logic, RPC handlers, and client architecture in this project are derived from LuaTools.
* **[Velopack](https://github.com/velopack/velopack)** — Desktop installer and update framework.
* **[WPF UI](https://github.com/lepoco/wpfui)** — Fluent design system controls for WPF.

## License

This project is licensed under the [MIT License](LICENSE).
