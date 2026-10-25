# Third-party notices

CloudSBiian itself is licensed under the MIT License (see `LICENSE`). It uses or redistributes the following components.

| Component | Used as | License | Notes |
|---|---|---|---|
| **WinFsp** (Windows File System Proxy) — https://github.com/winfsp/winfsp | Kernel driver installed separately (`dependencies\winfsp.msi` is the unmodified official MSI) and the `winfsp.net` 2.0 .NET binding (NuGet) | GPLv3 **with FLOSS exception** | The FLOSS exception allows free/open-source software under an OSI license such as MIT to use WinFsp. Distributing CloudSBiian under a non-OSI license would require a commercial WinFsp license. Source: https://github.com/winfsp/winfsp |
| **rclone** — https://rclone.org | Separate executable `rclone\rclone.exe`, launched as a child process | MIT | Unmodified official build (`rclone-current-windows-amd64.zip`). |
| **Microsoft.Data.Sqlite** / **SQLitePCLRaw** / **SQLite** | Database | MIT / Apache-2.0 / Public domain | NuGet packages. |
| **System.Security.Cryptography.ProtectedData** | DPAPI wrapper | MIT | NuGet package (.NET). |
| **.NET runtime & WPF** | Self-contained runtime in the packages | MIT | https://github.com/dotnet |
| **Inno Setup** | Builds the installer (not redistributed as a library) | Inno Setup License | https://jrsoftware.org |
| **xUnit, Microsoft.NET.Test.Sdk** | Tests only (not shipped) | Apache-2.0 / MIT | |

Optional, user-selected: an **rclone fork with a `terabox` backend** is *not* distributed with CloudSBiian. If you configure one in Settings, its license and terms are those of that project; it is not affiliated with rclone or TeraBox.

Trademarks (Google Drive, OneDrive, Dropbox, TeraBox, Steam, Epic Games, EA, Ubisoft, Windows) belong to their owners and are used only to describe compatibility.
