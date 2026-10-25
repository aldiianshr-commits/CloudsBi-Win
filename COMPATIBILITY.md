# Kompatibilitas

## Sistem operasi

| Platform | Status |
|---|---|
| Windows 11 x64 | Ditargetkan. Tidak diuji langsung di CI (lihat di bawah). |
| Windows 10 x64 1809+ | Ditargetkan (manifest, API, `MinVersion` installer 10.0.17763). Tidak diuji langsung di CI. |
| Windows Server 2025 x64 | **Diuji di CI** (runner GitHub `windows-latest`): build, unit, integrasi WinFsp, UI self-test, install/uninstall. |
| Windows ARM64 / x86 | Tidak didukung (build x64 saja; WinFsp ARM64 ada tetapi belum diuji). |

## Dependensi runtime

| Komponen | Versi | Wajib untuk |
|---|---|---|
| WinFsp | ≥ 2.0 (diuji: 2.1 via Chocolatey; installer menyertakan rilis terbaru) | Virtual drive |
| rclone | Diuji dengan versi terbaru saat CI berjalan | Google Drive, OneDrive, Dropbox, SFTP, S3, SMB, TeraBox (fork) |
| .NET 10 | Sudah termasuk (self-contained) | — |

## Provider

Lihat tabel status di [README.md](README.md#status-provider-jujur). Ringkas: WebDAV & Local/NAS **Supported**; SFTP & S3 diuji terhadap server lokal (Experimental karena belum diuji terhadap layanan komersial); Google Drive/OneDrive/Dropbox Experimental (tidak diuji dengan akun asli); TeraBox Blocked tanpa backend rclone pihak ketiga.

## Aplikasi & game

| Kategori | Cloud Direct / Hybrid | Full Local |
|---|---|---|
| Aplikasi/game portable tanpa DRM | Diharapkan berjalan; performa tergantung jaringan & pola baca | Ya |
| Game Steam/Epic/EA/Ubisoft yang butuh launcher | Hanya jika launcher berjalan dan menerima lokasi tersebut — tidak dijamin | Lebih mungkin; sebaiknya instal lewat launcher resmi |
| Anti-cheat (EAC, BattlEye, EA AC) | Kemungkinan besar gagal | Gunakan instalasi lokal resmi |

Tidak ada game tertentu yang telah diuji oleh proyek ini; tes otomatis menjalankan `cmd.exe` sebagai aplikasi uji yang aman.
