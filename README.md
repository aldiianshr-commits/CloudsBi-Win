# CloudSBiian

**Virtual drive cloud untuk Windows 10/11 (x64) dengan cache lokal cerdas dan library game/aplikasi.**

CloudSBiian menghubungkan beberapa layanan cloud storage ke Windows, menampilkannya sebagai huruf drive (mis. `X:`) di File Explorer, menyimpan blok data yang sering dipakai di disk lokal (NTFS) dengan batas kapasitas yang Anda tentukan, memprediksi file yang akan dibutuhkan, dan menjalankan game/aplikasi yang file-nya disimpan di cloud — langsung dari drive, dengan persiapan cache, atau setelah disalin ke lokal.

> Kapasitas **tidak unlimited**. Ruang cloud mengikuti kuota akun yang terhubung; cache memakai ruang disk fisik sebesar batas yang Anda pilih.

## Unduhan

Build terbaru (dibuat dan diuji otomatis di GitHub Actions `windows-latest`) ada di
[Releases → latest-build](https://github.com/aldiianshr-commits/CloudsBi-Win/releases/tag/latest-build):

| Artefak | Isi |
|---|---|
| `CloudSBiian-Setup.exe` | Installer (Inno Setup). Memasang aplikasi, `rclone\rclone.exe` resmi, dan (opsional, terlihat sebagai task) driver WinFsp. |
| `CloudSBiian-Portable-x64.zip` | Versi portable. Data disimpan di folder `data` di sebelah exe. WinFsp MSI disertakan di `dependencies\`. |
| `CloudSBiian.exe` | Executable tunggal self-contained, rclone resmi tertanam di dalamnya (diekstrak saat start). |

Persyaratan: Windows 10 1809+ / Windows 11 x64, **WinFsp 2.0 atau lebih baru** untuk virtual drive (tanpa WinFsp, fitur lain tetap berjalan dan aplikasi menjelaskan cara memasangnya).

## Status provider (jujur)

| Provider | Status | Cara |
|---|---|---|
| WebDAV | **Supported** | Klien native, diuji di CI terhadap server WebDAV (rclone). |
| Local / NAS (folder atau `\\server\share`) | **Supported** | Native, diuji otomatis. |
| SFTP | Experimental | Backend rclone `sftp`; diuji di CI terhadap server SFTP lokal (rclone). |
| S3-compatible | Experimental | Backend rclone `s3`; diuji di CI terhadap server S3 lokal (rclone). |
| Google Drive / OneDrive / Dropbox | Experimental | Backend rclone resmi (OAuth di browser). Adapter diuji, **belum diuji dengan akun asli** oleh proyek ini. |
| SMB (via rclone) | Experimental | Belum diuji otomatis. |
| **TeraBox** | **Blocked** secara default | Tidak ada API publik TeraBox yang terverifikasi untuk aplikasi pihak ketiga, dan rclone resmi tidak punya backend `terabox`. Jika Anda mengarahkan Settings → rclone ke build rclone yang menyertakan backend `terabox` (fork tidak resmi), status menjadi **Experimental**. Multi-akun TeraBox (akun terpisah, sesi/cache/antrean terpisah) diuji dengan backend tiruan, bukan dengan TeraBox asli. |

Detail lengkap: [PROVIDER_SETUP.md](PROVIDER_SETUP.md), [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).

## Fitur utama

- **Virtual drive (WinFsp)**: pilih huruf drive yang tersedia, satu folder per akun atau satu akun di root, mode read-only, mode network drive, mount otomatis saat start. Baca acak (random access) via HTTP Range bila backend mendukung; jika tidak, file diunduh berurutan sekali (tidak diklaim sebagai streaming).
- **Multi-akun**: tambah akun sebanyak yang diinginkan, termasuk beberapa akun TeraBox/Drive yang sama. Setiap akun punya ID internal, kredensial (DPAPI), remote rclone, cache, antrean, dan status error sendiri.
- **Cache engine**: anggaran 10/20/30/40 GB atau custom, batas minimum ruang disk kosong, cache blok + metadata + listing folder (bisa dibuka offline), pinning, pengecualian (glob), LRU, verifikasi SHA-256, pemulihan setelah crash, penghitungan ruang **fisik**.
- **Write-back aman**: perubahan disimpan dulu di area staging lokal, diunggah di latar belakang dengan status `PendingUpload → Uploading → Synced / RetryRequired / Conflict / Offline`. Perubahan yang belum terunggah tidak pernah dihapus oleh eviction atau "Clear cache".
- **Prediction engine (heuristik, bukan ML)**: read-ahead sekuensial, statistik "file B biasanya dibuka setelah A", dan jejak (trace) blok yang dibaca saat game diluncurkan. Prioritas P0–P3, P0 selalu menang dan dapat membatalkan prefetch.
- **Game Library**: Cloud Direct, Hybrid Optimized, Full Local (salin + verifikasi SHA-256), Optimize & Prepare, deteksi petunjuk kompatibilitas (anti-cheat, launcher). Tidak melewati DRM/anti-cheat.
- **Monitoring**: kecepatan aktual, hit/miss cache, antrean, latensi terukur; tampil N/A bila belum ada data.
- **11 halaman UI** (tema gelap/terang): Dashboard, Cloud Accounts, Virtual Drive, File Browser, Cache Manager, Prediction Engine, Game Library, Transfer Queue, Monitoring, Settings, Logs & Diagnostics.

## Build dari source

```powershell
dotnet build CloudSBiian.slnx -c Release
dotnet test tests/CloudSBiian.Tests -c Release
dotnet test tests/CloudSBiian.IntegrationTests -c Release   # butuh WinFsp + rclone, kalau tidak di-skip
./build/package.ps1 -Version 0.1.0                            # butuh Inno Setup 6
```

Butuh .NET SDK 10. Lihat [BUILD_STATUS.md](BUILD_STATUS.md), [CONTRIBUTING.md](CONTRIBUTING.md).

## Dokumentasi

[ARCHITECTURE](ARCHITECTURE.md) · [BUILD_STATUS](BUILD_STATUS.md) · [TEST_REPORT](TEST_REPORT.md) · [BENCHMARKS](BENCHMARKS.md) · [COMPATIBILITY](COMPATIBILITY.md) · [KNOWN_LIMITATIONS](KNOWN_LIMITATIONS.md) · [SECURITY](SECURITY.md) · [PROVIDER_SETUP](PROVIDER_SETUP.md) · [CACHE_ENGINE](CACHE_ENGINE.md) · [VIRTUAL_DRIVE](VIRTUAL_DRIVE.md) · [GAME_LAUNCHER](GAME_LAUNCHER.md) · [TROUBLESHOOTING](TROUBLESHOOTING.md) · [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)

## Lisensi

MIT — lihat [LICENSE](LICENSE). Komponen pihak ketiga (WinFsp, rclone, SQLite, dll.) memakai lisensinya masing-masing, lihat [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winfsp (dipakai di bawah GPLv3 FLOSS exception; lihat [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
