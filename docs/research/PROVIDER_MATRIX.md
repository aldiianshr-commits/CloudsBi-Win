# Provider matrix

Diperiksa: **2026-10-26**. Setiap baris memisahkan tiga hal: (a) apa yang dikatakan dokumentasi resmi provider, (b) apa yang dipakai CloudSBiian, dan (c) apa yang benar-benar sudah diuji. Status mengikuti `ProviderRegistry.cs` dan bukti CI run #21 (commit `44eb35a`, branch `ci-logs` → `ci/summary.txt`).

Arti status: **Supported** = ada dan teruji otomatis untuk kemampuan yang diklaim; **Experimental** = ada, pengujian terbatas atau belum terhadap layanan asli; **Blocked** = tidak bisa diwujudkan secara andal sekarang; **Planned** = belum dibuat.

## Ringkasan

| Provider | Backend di CloudSBiian | Auth | Range read | Status | Diuji terhadap |
|---|---|---|---|---|---|
| WebDAV | Klien native (`WebDavProvider`) | Basic/Digest lewat form | Ya, HTTP `Range`; diturunkan otomatis bila server membalas 200 | **Supported** | Server `rclone serve webdav` lokal di CI [REAL-PROTOCOL] |
| Local / NAS folder (UNC) | Native (`LocalFolderProvider`) | Hak akses Windows | Ya (file seek) | **Supported** | Unit test [MOCK] + filesystem lokal |
| SFTP | rclone `sftp` | Password atau kunci, disimpan DPAPI | Ya, lewat `--rc-serve` + `Range`, diprobe saat connect | **Experimental** | Server `rclone serve sftp` lokal di CI [REAL-PROTOCOL] |
| S3-compatible | rclone `s3` | Access key/secret | Ya, satu range per GET | **Experimental** | Server `rclone serve s3` lokal di CI [REAL-PROTOCOL] |
| Google Drive | rclone `drive` (Drive API resmi) | OAuth 2.0 lewat `rclone authorize` di browser | Ya untuk file biner; **tidak** untuk ekspor Google Docs | **Experimental** | Belum pernah dengan akun asli |
| OneDrive | rclone `onedrive` (Microsoft Graph) | OAuth 2.0 lewat `rclone authorize` | Ya, pada URL unduhan pra-otentikasi | **Experimental** | Belum pernah dengan akun asli |
| Dropbox | rclone `dropbox` (Dropbox API v2) | OAuth 2.0 lewat `rclone authorize` | Ya (`Range` pada `/2/files/download`) | **Experimental** | Belum pernah dengan akun asli |
| SMB | rclone `smb` | User/password | Diprobe saat connect | **Experimental** | Tidak diuji |
| Other rclone backend | Backend apa pun di rclone yang dipakai | Bergantung backend | Diprobe saat connect | **Experimental** | Tidak diuji per backend |
| TeraBox | rclone `terabox` — **tidak ada di rclone resmi** | Cookie sesi web (fork tidak resmi) | Tidak terverifikasi | **Blocked** (Experimental hanya bila rclone yang dipilih punya backend `terabox`) | Tidak pernah dengan akun asli |

Untuk semua provider berbasis rclone, CloudSBiian membaca lewat `rclone rcd --rc-serve` di 127.0.0.1 dengan header `Range`. Saat connect, `RcloneProvider.ProbeRangeSupportAsync` meminta range dari file yang diketahui; bila balasannya bukan 206, kapabilitas `RangeRead` dihapus dan file dibaca berurutan (tidak diklaim sebagai streaming).

## Detail per provider

### WebDAV
- **API**: RFC 4918 (PROPFIND, MKCOL, MOVE, DELETE, PUT), range menurut HTTP (RFC 9110). Kuota lewat RFC 4331 bila server menyediakannya.
- **Operasi di CloudSBiian**: list, stat, baca + Range, upload, delete, rename/move, mkdir, kuota.
- **Rate limit**: bergantung server; tidak ada batas dari protokol.
- **Multi-sesi**: setiap akun punya `HttpClient` dan kredensial sendiri.
- **Lisensi**: protokol terbuka; implementasi milik proyek (MIT).
- **Bukti**: `NativeWebDav_AgainstRcloneWebDavServer` (semua operasi + password salah → `AuthenticationFailed`), lulus di run #21.

### SFTP
- **Backend**: rclone `sftp` (MIT).
- **Range**: protokol SFTP membaca pada offset; lewat rclone tersedia sebagai HTTP Range di `--rc-serve`.
- **Bukti**: `Sftp_ThroughRcloneBackend_WithPasswordSecret` lulus di run #21. Status tetap Experimental karena belum diuji terhadap server SFTP pihak ketiga (OpenSSH di Windows/Linux). Kandidat naik ke Supported setelah itu.

### S3-compatible
- **API**: AWS S3 `GetObject` dengan header `Range`; S3 tidak mendukung beberapa range dalam satu GET. Sumber: [AWS SDK — GetObjectRequest.range](https://docs.aws.amazon.com/sdk-for-kotlin/api/latest/s3/aws.sdk.kotlin.services.s3.model/-get-object-request/range.html).
- **Bukti**: `S3_ThroughRcloneBackend_AgainstRcloneS3Server` (list, Range, upload, delete) lulus di run #21. Belum diuji terhadap AWS, MinIO, Wasabi, atau R2.

### Google Drive
- **API**: Google Drive API v3 lewat rclone `drive`.
- **Range**: unduhan file biner mendukung byte range lewat header `Range`; ekspor dokumen Google Workspace tidak mendukung unduhan parsial dan dibatasi 10 MB. Sumber: [Download and export files](https://developers.google.com/drive/api/v2/manage-downloads).
- **Rate limit** (dokumentasi resmi): 1.000.000 unit kuota per menit per proyek dan 325.000 per menit per pengguna per proyek; unduhan berbiaya 200 unit; batas unggah 750 GB per hari untuk pengguna Google Workspace; 403/429 harus ditangani dengan exponential backoff. Sumber: [Usage limits](https://developers.google.com/workspace/drive/api/guides/limits).
- **Catatan**: kuota tersebut berlaku untuk *client ID* OAuth. Bila memakai client ID bawaan rclone, kuota dibagi dengan semua pengguna rclone; pengguna sebaiknya membuat client ID sendiri (lihat PROVIDER_SETUP.md).
- **Multi-akun**: setiap akun adalah remote rclone sendiri (`csb_<id>`) dengan token sendiri.
- **Bukti**: tidak ada pengujian dengan akun asli. Login OAuth butuh browser interaktif.

### OneDrive
- **API**: Microsoft Graph lewat rclone `onedrive`.
- **Range**: `GET /content` membalas 302 ke URL pra-otentikasi; header `Range` harus dikirim ke URL itu, bukan ke `/content`. Server bisa mengabaikan range dan membalas 200 dengan seluruh file. URL tersebut cepat kedaluwarsa. Sumber: [Download driveItem content](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content).
- **Rate limit**: throttling ditandai 429 + `Retry-After`; tunggu sesuai header, atau exponential backoff bila header tidak ada. Angka batas khusus OneDrive tidak dipublikasikan di halaman ini. Sumber: [Microsoft Graph throttling guidance](https://learn.microsoft.com/en-us/graph/throttling).
- **Bukti**: tidak ada pengujian dengan akun asli.

### Dropbox
- **API**: Dropbox API v2 lewat rclone `dropbox`.
- **Range**: SDK resmi Dropbox menyediakan `range(start, length)` pada builder unduhan yang mengirim header `Range`. Sumber: [DbxDownloadStyleBuilder (Dropbox Java SDK)](https://dropbox.github.io/dropbox-sdk-java/api-docs/v2.1.x/com/dropbox/core/v2/DbxDownloadStyleBuilder.html).
- **Rate limit**: [unverified] Dropbox tidak mempublikasikan angka batas di halaman yang bisa saya buka; panduan error resmi (`docs.dropboxapi.com/dropbox-api/docs/error-handling`) tidak dapat diakses dari lingkungan ini. Penanganan 429 di sisi rclone juga [unverified] untuk backend ini dan belum diuji oleh proyek ini.
- **Bukti**: tidak ada pengujian dengan akun asli.

### SMB
- **Backend**: rclone `smb`. Untuk share yang sudah terpasang di Windows, "Local / NAS folder" lebih disarankan.
- **Bukti**: tidak diuji (tercatat di TEST_REPORT.md).

### TeraBox

**Status: Blocked** dengan rclone resmi. Tidak ada jalur yang andal dan didukung saat ini.

Yang sudah diperiksa:

1. **Dokumentasi resmi TeraBox.** Ada halaman "TeraBox Open Platform Integration Document" di [terabox.com/integrations/docs](https://www.terabox.com/integrations/docs). Isinya dirender lewat JavaScript; saat diambil hanya metadata halaman yang terbaca, tanpa endpoint, cara auth, syarat akses, atau batas laju. **[unverified]**: belum diketahui apakah platform ini terbuka untuk aplikasi pihak ketiga atau hanya mitra yang disetujui. Langkah berikutnya: buka halaman itu di browser dan, bila perlu, ajukan kredensial aplikasi ke TeraBox.
2. **rclone resmi.** Pada `master` (commit `81e7372`, 2026-10-20, VERSION `v1.76.0`) tidak ada direktori `backend/terabox` (diperiksa dengan `git ls-tree HEAD backend/`). Tes CI `Catalog_ListsOfficialBackends_AndTeraBoxStatusIsHonest` juga memastikan rclone v1.75.2 yang dibundel tidak punya backend `terabox`.
3. **PR upstream.** [rclone PR #8508 "Terabox backend"](https://github.com/rclone/rclone/pull/8508) masih terbuka tanpa keputusan merge. Backend ini memakai cookie sesi web (`ndus`), bukan OAuth resmi. Penulisnya menyebut integrasi resmi butuh *client id/secret* dari TeraBox yang belum ia dapatkan. Maintainer meminta `test_all` lulus sebelum review akhir. Pengguna melaporkan delete yang kadang ditolak ("frequency of operation is too soon") dan rename folder yang gagal saat mount.
4. **Fork pihak ketiga.** [bclone](https://github.com/BenjiThatFoxGuy/bclone) (MIT, menyatakan aktif dipelihara) menyertakan backend `terabox`. [Issue #45](https://github.com/BenjiThatFoxGuy/bclone/issues/45) (dibuka 2026-10-26, masih terbuka) melaporkan: errno 31034 saat di-throttle, errno 111 saat operasi bersamaan, timeout yang di-hardcode, dan MD5 basi dari API sehingga hash dinonaktifkan. Juga [rclone-extra](https://github.com/gulp79/rclone-extra), status pemeliharaan tidak diperiksa.

Kesimpulan:
- **Backend yang dipakai**: tidak ada secara default. Pengguna boleh memilih sendiri rclone yang punya backend `terabox` di Settings; status lalu menjadi Experimental dan cookie diisi sendiri oleh pengguna. CloudSBiian tidak mengambil cookie dari browser.
- **Operasi yang tersedia**: sepenuhnya bergantung pada fork itu; belum diuji oleh proyek ini.
- **Multi-akun**: logika isolasi (ID, remote `csb_<id>`, kredensial, cache, antrean, retry terpisah) diuji dengan backend tiruan [MOCK] dan dua akun bertipe TeraBox di drive WinFsp nyata [REAL-DRIVER]. **Tidak pernah** diuji dengan akun TeraBox asli.
- **Masa berlaku sesi**: [unverified]. Fork berbasis cookie mewarisi masa berlaku sesi web TeraBox, yang tidak didokumentasikan.
- **Range read**: [unverified] untuk TeraBox. CloudSBiian akan memprobe dan menurunkan ke baca berurutan bila tidak didukung.
- **Risiko**: memakai sesi web dengan klien tidak resmi bisa melanggar ketentuan TeraBox dan rusak kapan saja saat TeraBox mengubah API web-nya.

## Lisensi komponen redistribusi

| Komponen | Lisensi | Syarat yang relevan | Sumber |
|---|---|---|---|
| rclone | MIT, "Copyright (C) 2012 by Nick Craig-Wood" | Sertakan teks lisensi (dikirim sebagai `rclone\COPYING.txt`). | `COPYING` di rclone commit `81e7372` |
| WinFsp (driver + MSI) dan `winfsp.net` | GPLv3 + FLOSS exception | Boleh menautkan DLL WinFsp dan mendistribusikan installer resmi **tanpa modifikasi**, asalkan (1) CloudSBiian berlisensi FLOSS (MIT: ya), (2) menampilkan "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos" + tautan repo di UI dan dokumen pengguna, (3) tidak didistribusikan bersama perangkat lunak proprietary. | `License.txt` di WinFsp commit `4425d48`; [winfsp.dev/com](https://winfsp.dev/com/) |
| Fork rclone dengan `terabox` | Bergantung fork (bclone: MIT) | Tidak didistribusikan oleh CloudSBiian. | Repo fork |

Syarat (2) WinFsp belum dipenuhi sebelum sesi ini (teks pemberitahuan tidak ada di UI maupun README). Diperbaiki di branch `m1-research-adr`: kartu **Settings → About & licenses** dan footer di setiap dokumen pengguna.
