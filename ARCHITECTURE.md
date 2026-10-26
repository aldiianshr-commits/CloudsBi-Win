# Arsitektur

## Proyek

| Proyek | Target | Tanggung jawab |
|---|---|---|
| `CloudSBiian.Core` | net10.0 | Kontrak `ICloudStorageProvider`, model akun, `CloudPath` (normalisasi & validasi path), SQLite (`AppDatabase` + migrasi), settings, logging dengan redaksi, penyimpanan rahasia (DPAPI), Job Object untuk proses anak. |
| `CloudSBiian.Monitoring` | net10.0 | `MetricsRegistry` (counter + latensi), `TransferTracker` (kecepatan terukur per transfer/akun), `ActivityLog` (riwayat di SQLite), `MetricsSampler` (sampel per detik untuk grafik). |
| `CloudSBiian.CloudProviders` | net10.0 | `ProviderRegistry` (status jujur), `AccountManager` (siklus hidup per akun), adapter: `RcloneProvider` (+ `RcloneDaemon`, `RcloneBackendCatalog`, `RcloneRemoteConfigurator`, `RcloneAuthorizer`), `WebDavProvider` (native), `LocalFolderProvider`, `InMemoryProvider` (tes). |
| `CloudSBiian.CacheEngine` | net10.0 | `ChunkStore` (blok di NTFS + SQLite + cache RAM), `CacheService` (read-through, anggaran, LRU, pin, pengecualian), `PriorityLimiter`, `BandwidthLimiter`, `MetadataCache`, `StagingManager`, `UploadQueue`. |
| `CloudSBiian.PredictionEngine` | net10.0 | `AccessProfileStore` (statistik di SQLite), `PredictionService` (read-ahead, co-access, trace peluncuran, antrean prefetch P1–P3). |
| `CloudSBiian.VirtualDrive` | net10.0-windows | `DriveCore` (semantik file system, independen dari WinFsp), `CloudFileSystem` (adapter `winfsp.net`), `DriveManager` (mount/unmount, deteksi WinFsp & huruf drive). |
| `CloudSBiian.GameLauncher` | net10.0 | `LibraryStore`, `LaunchService` (start/stop/pemulihan), `PrepareService` (Optimize & Prepare, instal Full Local terverifikasi), `CompatibilityScanner`. |
| `CloudSBiian.Desktop` | net10.0-windows (WPF) | `AppHost` (composition root, sleep/resume, jaringan), MVVM, 11 halaman, dialog, self-test UI. |
| `CloudSBiian.Tests` | xUnit | Unit test dengan provider tiruan. |
| `CloudSBiian.IntegrationTests` | xUnit | Mount WinFsp nyata; rclone/WebDAV/SFTP/S3 dengan server lokal. |

Dependensi satu arah: Core ← Monitoring ← (CloudProviders, CacheEngine) ← PredictionEngine ← (VirtualDrive, GameLauncher) ← Desktop.

## Alur baca (File Explorer → cloud)

```
Explorer → WinFsp (kernel) → CloudFileSystem.Read → DriveCore.Read
   ├─ file punya staging (dimodifikasi/pending) → baca dari staging lokal
   └─ CacheService.ReadAsync(akun, entry, offset, P0)
        ├─ ChunkStore.TryRead  (RAM → disk + SHA-256)  ── hit
        └─ miss → PriorityLimiter (P0 bisa membatalkan prefetch) → provider.OpenReadAsync(Range)
                  → simpan blok (temp + rename, SHA-256, SQLite) → kembalikan data
```
Blok default 4 MiB. Backend tanpa Range: file diunduh sekali dari awal dan dipotong menjadi blok.

## Alur tulis (write-back)

```
Create/Write → DriveCore.EnsureStaging (unduh isi lengkap dulu bila file sudah ada)
   → StagingFile (berbagi antar-handle, sidecar JSON untuk pemulihan)
Cleanup (handle terakhir) → UploadQueue.Enqueue(base identity)
UploadWorker → stat remote → identity berbeda? → Conflict (tidak menimpa)
            → upload (rclone copyfile / PUT / stream) → Synced → hapus staging, invalidasi cache
            → gagal: Offline (jaringan) / PendingUpload + backoff / RetryRequired (auth, batas percobaan)
```

## Isolasi multi-akun

- ID internal unik `kind_xxxxxxxxxxxx`; rahasia di file DPAPI per akun dengan entropi berbasis ID.
- Remote rclone per akun (`csb_<id>`) di konfigurasi **in-memory** daemon rclone pribadi.
- Kunci cache = SHA-256(akun + path); direktori cache dan staging per akun.
- Antrean, status, error, dan prediksi selalu dikunci dengan ID akun.
- Kegagalan satu akun hanya mengubah status akun itu (lihat `AccountTests`).

## Proses rclone

`RcloneDaemon` menjalankan `rclone rcd` pada `127.0.0.1:<port acak>` dengan user/password acak lewat variabel lingkungan (tidak terlihat di daftar proses), `--rc-serve` untuk baca Range, `--config ""` (tanpa file konfigurasi). Proses diikat ke Job Object sehingga ikut berhenti jika CloudSBiian berhenti/crash. Token OAuth yang diperbarui rclone disimpan kembali ke DPAPI secara berkala.

Dependensi rclone dikelola oleh `AccountManager.CheckRcloneAsync`: `RcloneLocator.Locate` (Settings → salinan bawaan → `tools\rclone` hasil unduhan → CloudSBiian terinstal → winget/Scoop/Chocolatey/Program Files → PATH proses & registry) → `rclone version` (≥ v1.60) → start daemon → `config/providers`. Hasilnya `RcloneStatus` (`Missing`/`Unusable`/`StartFailed`/`Ready`, path, sumber, versi, semua lokasi yang diperiksa) dengan event `RcloneStatusChanged`; `ProviderRegistry.Effective` memakai status itu dan daftar backend nyata. `RcloneInstaller` mengunduh rilis resmi (verifikasi `SHA256SUMS`, validasi exe) ke `tools\rclone`. UI: `RcloneDependencyViewModel` + `RcloneDependencyPanel` (Download / Browse / Re-check / Details) di Cloud Accounts, Settings, dan Add Account.

## Data aplikasi

`%LOCALAPPDATA%\CloudSBiian\` (atau `data\` pada mode portable): `cloudsbiian.db` (SQLite WAL), `settings.json`, `secrets\*.secret` (DPAPI), `logs\`, `tools\rclone\` (hanya bila "Download rclone" dipakai), `cache\blocks\<akun>\`, `cache\staging\<akun>\`.
