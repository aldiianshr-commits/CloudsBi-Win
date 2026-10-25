# Test report

Sumber: GitHub Actions run untuk commit terbaru di `main` (`.github/workflows/ci.yml`, runner `windows-latest` = Windows Server 2025 x64). Hasil mentah (TRX diringkas, log, benchmark, self-test) dipublikasikan setiap run ke branch [`ci-logs`](https://github.com/aldiianshr-commits/CloudsBi-Win/tree/ci-logs) (`ci/summary.txt`, `ci/integration.log`, `ci/ui-selftest*.txt`, `ci/install-test.txt`, `ci/bench-results.json`).

**Pemisahan hasil:**
- **[MOCK]** = backend `InMemoryProvider` (tanpa jaringan). Menguji logika CloudSBiian, bukan provider sungguhan.
- **[REAL-PROTOCOL]** = protokol sungguhan terhadap server lokal (rclone `serve webdav/sftp/s3`, backend rclone `local`) — tanpa akun cloud.
- **[REAL-DRIVER]** = driver WinFsp sungguhan; operasi lewat API Windows (`System.IO`) pada drive yang di-mount.
- Pengujian terhadap **akun cloud asli (Google Drive, OneDrive, Dropbox, TeraBox) TIDAK dijalankan** — lihat bagian terakhir.

## Ringkasan (build 0.1.17, commit `3e5f11e`)

| Suite | Total | Lulus | Gagal | Skip |
|---|---|---|---|---|
| Unit (`CloudSBiian.Tests`) [MOCK] | 83 | 83 | 0 | 0 |
| Integrasi (`CloudSBiian.IntegrationTests`) [REAL-PROTOCOL]/[REAL-DRIVER] | 14 | 14 | 0 | 0 |
| UI self-test (paket portable): 11 halaman × 2 tema + dialog Add Account untuk 10 jenis provider | 32 layar | 32 | 0 | — |
| Packaging (exe single-file, zip portable, installer Inno Setup) | 3 artefak | 3 | 0 | — |
| Installer: install diam, self-test aplikasi terinstal, uninstall diam, self-test exe single-file | 12 pemeriksaan | 12 | 0 | — |

## Unit test [MOCK]

**Multi-akun / TeraBox (kind TeraBox, backend tiruan)** — `AccountTests`
- AddingSeveralTeraBoxAccounts_GivesIndependentEntries — 3 akun TeraBox: ID berbeda, semua terhubung, rahasia terpisah, provider berbeda.
- SuggestDisplayName_SkipsUsedNames — "TeraBox", "TeraBox 2" → saran "TeraBox 3".
- SessionExpiry_AffectsOnlyThatAccount — sesi kedaluwarsa akun A; akun B tetap Connected; Reconnect memulihkan A.
- FailedConnect_DoesNotBlockOtherAccounts — kegagalan auth satu akun tidak menghalangi ConnectAll akun lain.
- TestConnection_ReportsRateLimitAndRecovers — rate limit terdeteksi lalu pulih.
- RemovingOneAccount_KeepsTheOthersAndTheirCache — hapus A: kredensial & cache A hilang, B utuh, file cloud A tidak dihapus.
- AccountList_SurvivesRestart — daftar & nama akun pulih setelah restart.
- ConcurrentTransfersOnTwoAccounts_DoNotInterfere — dua akun, path sama, unduh paralel, data & kunci cache terpisah.

**Cache engine** — `CacheTests`: read-through + hit kedua; 300 pembacaan acak dibanding sumber; batas anggaran dengan eviction LRU; pin tidak di-evict; blok rusak terdeteksi & diunduh ulang; file sumber berubah → invalidasi; path sama di dua akun terpisah; backend tanpa Range diunduh berurutan sekali; batas ruang disk minimum (tidak menyimpan, tetap membaca); pengecualian glob; clear cache tidak menyentuh cloud & staging; VerifyAll menghapus blok hilang & file yatim; cache bertahan setelah restart & file temp dibersihkan; pola glob.

**Upload / konsistensi** — `UploadQueueTests`: upload & staging dihapus; retry otomatis setelah rate limit; offline → Offline, data tetap, lalu terunggah; sesi kedaluwarsa → RetryRequired → Retry manual; perubahan remote → Conflict tanpa menimpa + Keep both; Keep local menimpa setelah keputusan eksplisit; upload yang terputus dilanjutkan setelah restart; file staging yang belum masuk antrean ditemukan untuk pemulihan.

**Prediksi & penjadwalan** — `PredictionTests`, `SchedulingTests`: read-ahead sekuensial; co-access dipelajari lalu di-prefetch; prediksi nonaktif tidak mem-prefetch; trace peluncuran merekam urutan baca; P0 membatalkan prefetch; slot cadangan untuk P0/P1; pembatas bandwidth.

**Semantik drive (tanpa driver)** — `DriveCoreTests`: satu folder per akun (nama disanitasi & unik); baca lewat drive; not-found vs path-not-found; create/tulis/close → upload; tulis parsial ke file besar mempertahankan isi lain; rename & delete ke cloud; rename file yang belum terunggah tetap lokal; folder tidak kosong & root tidak bisa dihapus; mount read-only menolak tulis; nama tidak valid disembunyikan & traversal ditolak; offline: data ter-cache tetap terbaca; ukuran volume dari kuota; pemetaan huruf besar/kecil Windows ke ejaan cloud (termasuk rename case-only).

**Game library** — `GameLauncherTests`: library persisten; launch `cmd.exe /c exit 3` → exit code 3 tercatat; stop menghentikan process tree; executable hilang → error jelas; validasi argumen; pemulihan status setelah restart; pemindai kompatibilitas (EAC, Steam); Optimize & Prepare menganalisis dan mem-prefetch trace (dan pin); instal Full Local menyalin + memverifikasi, instal ulang melewati file identik.

**Inti & keamanan** — `CloudPathTests`, `SecretTests`, `SettingsTests`: normalisasi path, penolakan `..`, nama perangkat & karakter tidak valid, path lokal tetap di root; redaksi rahasia/token; isolasi secret store; blob DPAPI akun A tidak bisa dibaca sebagai akun B; settings round-trip, validasi, fallback saat korup.

## Integrasi

| Tes | Jenis | Hasil |
|---|---|---|
| Catalog_ListsOfficialBackends_AndTeraBoxStatusIsHonest — rclone resmi punya drive/onedrive/dropbox/sftp/s3/webdav/smb/local; TeraBox = Blocked karena tidak ada backend `terabox` | REAL-PROTOCOL | Lulus |
| LocalBackend_ThroughAccountManager_SupportsAllOperations — daemon rclone in-memory config, list/stat/Range (diverifikasi 206)/upload stream/upload file (job)/move/mkdir/dir move/rmdir/delete/kuota | REAL-PROTOCOL | Lulus |
| TwoAccounts_GetSeparateRemotes — dua akun rclone, isi terpisah, disconnect satu tidak memengaruhi yang lain | REAL-PROTOCOL | Lulus |
| NativeWebDav_AgainstRcloneWebDavServer — klien WebDAV native, semua operasi + password salah → AuthenticationFailed | REAL-PROTOCOL | Lulus |
| Sftp_ThroughRcloneBackend_WithPasswordSecret — SFTP melalui AccountManager, password tersimpan DPAPI-store & teredaksi | REAL-PROTOCOL | Lulus |
| S3_ThroughRcloneBackend_AgainstRcloneS3Server — S3 (provider Other, path style), list/Range/upload/delete | REAL-PROTOCOL | Lulus |
| Mount_Browse_ReadSequentialAndRandom_Unmount | REAL-DRIVER (+mock backend) | Lulus |
| ConcurrentReaders_GetConsistentData — 8 pembaca paralel, handle kembali 0 | REAL-DRIVER | Lulus |
| ExplorerStyleWrites_ReachTheCloud — buat, overwrite, rename, mkdir, delete file & folder via System.IO | REAL-DRIVER | Lulus |
| UsedDriveLetter_IsRejected | REAL-DRIVER | Lulus |
| MultiAccountDrive_IsolatesAccountsInFolders — dua "TeraBox" (mock), file sama nama berbeda isi; move antar akun = salin+hapus | REAL-DRIVER | Lulus |
| ReadOnlyMount_RefusesWrites | REAL-DRIVER | Lulus |
| Disconnect_CachedDataStaysReadable_AndReconnectRestoresAccess | REAL-DRIVER | Lulus |
| Benchmark_ColdAndWarmSequentialRead | REAL-DRIVER | Lulus (angka: [BENCHMARKS.md](BENCHMARKS.md)) |

Bug nyata yang ditemukan oleh tes integrasi dan diperbaiki selama pengembangan: binding `winfsp.net` 2.2 menolak WinFsp 2.1 (diganti ke binding 2.0 + cek versi); WinFsp mengirim komponen path huruf besar saat rename sehingga file berpindah ke folder `/DOCS` yang salah pada backend case-sensitive (kini dipetakan ke ejaan asli).

## Installer

Diuji di CI (build 0.1.17, `ci/install-test.txt`):

```
setup exit code: 0
installed CloudSBiian.exe : True
installed rclone\rclone.exe : True
installed dependencies\winfsp.msi : True
installed LICENSE : True
installed THIRD_PARTY_NOTICES.md : True
installed app self-test exit code: 0
user data exists before uninstall: True
uninstall exit code: 0
program removed: True
user data kept after uninstall: True
single-file self-test exit code: 0
```

Bug yang ditemukan di sini dan diperbaiki: pesan uninstall memakai `MsgBox` yang tidak dapat ditekan pada mode diam sehingga uninstall diam menggantung; kini `SuppressibleMsgBox`.

## TIDAK diuji

- Akun Google Drive, OneDrive, Dropbox **asli** (OAuth interaktif tidak dapat dijalankan di CI).
- **TeraBox asli** dengan backend rclone pihak ketiga (tidak tersedia/terverifikasi; status Blocked).
- SMB melalui rclone.
- Siklus sleep/resume sungguhan (hanya putus/sambung disimulasikan).
- Game komersial nyata, anti-cheat, launcher (Steam/Epic/EA).
- Windows 10/11 desktop secara langsung, monitor DPI tinggi, instalasi WinFsp lewat installer CloudSBiian (di CI WinFsp sudah terpasang lebih dulu, jadi task tersebut tersembunyi).
- Pengujian beban jangka panjang (berhari-hari) dan disk benar-benar penuh (diuji lewat batas ruang kosong minimum).
