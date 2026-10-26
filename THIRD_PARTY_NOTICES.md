# Third-party notices

CloudSBiian itself is licensed under the MIT License (see `LICENSE`). It uses or redistributes the following components.

| Component | Used as | License | Notes |
|---|---|---|---|
| **WinFsp** (Windows File System Proxy) — https://github.com/winfsp/winfsp | Kernel driver installed separately (`dependencies\winfsp.msi` is the unmodified official MSI) and the `winfsp.net` 2.0 .NET binding (NuGet) | GPLv3 **with FLOSS exception** | The exception (WinFsp `License.txt`, checked 2026-10-26 at commit `4425d48`) permits linking a platform WinFsp DLL and distributing the **unmodified** official installer, provided that the software (1) is under a Free Software / Open Source Definition license (CloudSBiian: MIT), (2) shows the notice "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos" and a link to the WinFsp repository in its user interface and user-facing documentation (CloudSBiian: Settings → About & licenses, and the footer of each user document), and (3) is not linked or distributed with proprietary software. Distributing CloudSBiian under a non-FLOSS license, or bundling it with proprietary software, would require a commercial WinFsp license. Source: https://github.com/winfsp/winfsp/blob/master/License.txt |
| **rclone** — https://rclone.org | Separate executable `rclone\rclone.exe`, launched as a child process. Shipped in the installer, the portable zip and embedded in the single-file `CloudSBiian.exe`; can also be downloaded on request by the app ("Download rclone") into `%LOCALAPPDATA%\CloudSBiian\tools\rclone` | MIT | Unmodified official Windows build from `downloads.rclone.org`, SHA-256 checked against the release `SHA256SUMS`. The license text is shipped as `rclone\COPYING.txt` (in-app downloads keep rclone's `README.txt` and a `SOURCE.txt` with the license link). |
| **Microsoft.Data.Sqlite** / **SQLitePCLRaw** / **SQLite** | Database | MIT / Apache-2.0 / Public domain | NuGet packages. |
| **System.Security.Cryptography.ProtectedData** | DPAPI wrapper | MIT | NuGet package (.NET). |
| **.NET runtime & WPF** | Self-contained runtime in the packages | MIT | https://github.com/dotnet |
| **Inno Setup** | Builds the installer (not redistributed as a library) | Inno Setup License | https://jrsoftware.org |
| **xUnit, Microsoft.NET.Test.Sdk** | Tests only (not shipped) | Apache-2.0 / MIT | |

Optional, user-selected: an **rclone fork with a `terabox` backend** is *not* distributed with CloudSBiian. If you configure one in Settings, its license and terms are those of that project; it is not affiliated with rclone or TeraBox.

Trademarks (Google Drive, OneDrive, Dropbox, TeraBox, Steam, Epic Games, EA, Ubisoft, Windows) belong to their owners and are used only to describe compatibility.

---

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winfsp (dipakai di bawah GPLv3 FLOSS exception; lihat [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
