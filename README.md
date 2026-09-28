<div align="center">

<img src="src/DataJackUIGui/icon.ico" width="96" height="96" alt="DataJackUI Icon" />

# DataJackUI

A modern desktop interface for Steam manifest management, native hook injection, and store plugin integration.

[![Version](https://img.shields.io/github/v/release/NetrunnerGames/DataJackUI?color=06b6d4&label=release)](https://github.com/NetrunnerGames/DataJackUI/releases/latest)
[![Framework](https://img.shields.io/badge/.NET-8.0_WPF-6b21a8)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-059669)](LICENSE)

[Download Setup](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.0) • [Jack-in Steam Plugin](https://github.com/NetrunnerGames/Jack-in)

</div>

---

## Overview

**DataJackUI** is a Windows desktop application built with .NET 8 WPF designed for game library management, Steam manifest retrieval, native DLL hook administration, and inline Steam client plugin communication. It features an Acrylic dark glass interface, integrated Denuvo fix routines, and configurable DNS-over-HTTPS fallback resolution.

---

## Features

- **Dark Acrylic Window Styling**: Native Windows 10/11 Acrylic backdrop support with a dark-tinted background container grid.
- **Real-Time Store Strips**: Direct Steam storefront search queries filtered strictly for games to display Top Sellers and Popular New Releases.
- **Jack-in Steam Plugin Integration**: Native RPC backend matching the `Jack-in` Steam CEF store plugin for adding manifests directly from the Steam client.
- **IceBreaker Hook Management**: Interface for managing native `version.dll` hook binaries and CloudRedirect configurations.
- **Fixes Repository**: Game repair utilities and Denuvo fix application with automated file backups.
- **Cloudflare DoH Resolution**: Built-in DNS over HTTPS (`https://1.1.1.1/dns-query`) fallback mechanism for environments where standard DNS resolution is throttled or blocked.

---

## Releases & Installation

- **Installer**: Download `DataJackUI-win-Setup.exe` from [Releases](https://github.com/NetrunnerGames/DataJackUI/releases/tag/v2.00.0) for standard installation.
- **Portable**: Download `DataJackUI-win-Portable.zip` for a zero-installation package.
- **Steam CEF Plugin**: Download `plugin.zip` from the [Jack-in Repository](https://github.com/NetrunnerGames/Jack-in/releases/tag/v1.0) and extract into your Steam client plugins folder.

---

## Building from Source

### Requirements
- .NET 8.0 Desktop SDK
- Windows 10 or 11 (x64)

### Local Test Build
```powershell
git clone https://github.com/NetrunnerGames/DataJackUI.git
cd DataJackUI

# Publishes and launches local framework-dependent binary
.\run_local.ps1
```

### Packaging Release Assets
```powershell
# Compiles framework-dependent binaries and generates Velopack installer packages
.\build_release.ps1 -d 2.00.0 -p 1.0
```

---

## Acknowledgments

DataJackUI is developed using concepts and architecture adapted from open-source projects:

- **[LuaTools](https://github.com/madoiscool/LuaTools)** — Created by **`madoiscool`** and contributors. The manifest handling logic, RPC handlers, and client architecture in this project are derived from LuaTools.
- **[Velopack](https://github.com/velopack/velopack)** — Desktop installer and update framework.
- **[WPF UI](https://github.com/lepoco/wpfui)** — Fluent design system controls for WPF.

---

## License

This project is licensed under the [MIT License](LICENSE).
