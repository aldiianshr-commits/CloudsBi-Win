# ADR-0001: Virtual drive memakai filesystem WinFsp sendiri, bukan `rclone mount`

- Status: **Accepted** (mendokumentasikan keputusan yang sudah diimplementasikan; dicatat 2026-10-26)
- Konteks kode: `src/CloudSBiian.VirtualDrive` (`DriveCore`, `CloudFileSystem`, `DriveManager`), `src/CloudSBiian.CacheEngine`, `src/CloudSBiian.PredictionEngine`

## Konteks

CloudSBiian harus memasang beberapa akun cloud sebagai satu huruf drive, dengan cache lokal yang beranggaran, pinning, prefetch berbasis profil akses, prioritas P0 di atas prefetch, dan status sinkronisasi per file (Pending Upload sampai Conflict).

Ada dua pilihan:

1. **`rclone mount`** (rclone menjalankan WinFsp sendiri, cache lewat `--vfs-cache-mode`).
2. **Filesystem WinFsp milik proyek** di atas `ICloudStorageProvider`, dengan rclone (`rclone rcd`) hanya sebagai backend penyimpanan untuk provider yang tidak punya adapter native.

## Keputusan

Pilihan 2. Jalur baca Explorer → WinFsp → `CloudFileSystem` → `DriveCore` → `CacheService` → provider (lihat ARCHITECTURE.md). Setiap pembacaan melewati kode CloudSBiian.

## Alasan

| Kebutuhan | `rclone mount` | WinFsp sendiri |
|---|---|---|
| Cache blok dengan anggaran, LRU, pin, eksklusi, verifikasi SHA-256 | VFS cache rclone: anggaran dan umur ada, tetapi pin per file, eksklusi glob, dan verifikasi integritas per blok tidak bisa dikendalikan dari aplikasi | Dikendalikan penuh (`ChunkStore`, `CacheService`) dan diuji (`CacheTests`) |
| Prefetch dari profil akses + P0 membatalkan prefetch | Hanya read-ahead per file; tidak ada antrean prioritas lintas file yang bisa diatur aplikasi | `PredictionService` + `PriorityLimiter`, diuji di `PredictionTests` dan `SchedulingTests` |
| Data pending upload tidak pernah di-evict; clear cache tidak menyentuh cloud | Bergantung pada perilaku internal VFS rclone | Invarian di kode staging/antrean, diuji di `CacheTests` dan `UploadQueueTests` |
| Status per file (Pending/Uploading/Synced/Retry/Conflict/Offline) dan konflik tanpa menimpa | Tidak diekspos per file | `UploadQueue`, diuji |
| Satu drive, satu folder per akun, isolasi akun | Satu mount per remote atau remote `union`/`combine` | `DriveCore` memetakan folder per akun, diuji (`MultiAccountDrive_IsolatesAccountsInFolders`) |
| Kematangan semantik filesystem Windows (rename, case, handle) | Sangat matang, dipakai luas | Harus dibangun dan dirawat sendiri; dua bug nyata sudah ditemukan lewat tes integrasi (binding 2.2 vs WinFsp 2.1, pemetaan huruf besar saat rename) |
| Cakupan backend | Semua backend rclone | Sama, karena provider rclone tetap dipakai sebagai backend lewat `rclone rcd` |

Cache engine dan prediction engine yang diminta hanya bermakna bila jalur baca melewati kode kita. Dengan `rclone mount`, keduanya akan berupa lapisan yang tidak pernah dilewati Explorer.

## Konsekuensi

- Proyek menanggung risiko kebenaran filesystem (deadlock, handle bocor, semantik rename). Mitigasi: `DriveCore` independen dari WinFsp sehingga bisa diuji tanpa driver (`DriveCoreTests`), ditambah tes driver nyata di CI (`WinFspDriveTests`: browse, baca sekuensial/acak, 8 pembaca paralel dengan handle kembali 0, tulis gaya Explorer, read-only, disconnect/reconnect).
- Siklus sleep/resume sungguhan belum diuji (hanya putus/sambung yang disimulasikan).
- Bergantung pada `winfsp.net` 2.0 dan WinFsp ≥ 2.0; lisensi GPLv3 + FLOSS exception mengikat syarat distribusi (lihat `docs/research/PROVIDER_MATRIX.md`).
- Backend tanpa dukungan Range dibaca berurutan sekali lalu dipotong menjadi blok; ini tidak disebut streaming.

## Kapan keputusan ini ditinjau ulang

Bila tes integrasi menunjukkan masalah semantik filesystem yang berulang dan tidak bisa diperbaiki di `DriveCore`, atau bila kebutuhan cache/prefetch kustom dihapus dari lingkup produk.
