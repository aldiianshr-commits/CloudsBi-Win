# Menyiapkan provider

Semua akun ditambahkan di **Cloud Accounts → + Add Account** (atau **+ Add TeraBox Account**). Ulangi untuk menambah akun berikutnya dari provider yang sama — setiap akun independen.

Kolom umum:
- **Display name** — nama di aplikasi dan nama folder di drive (mode "All accounts").
- **Root folder** — hanya folder ini yang ditampilkan (default `/`).

## rclone

Provider Google Drive, OneDrive, Dropbox, SFTP, S3, SMB, TeraBox, dan "Other rclone backend" memakai [rclone](https://rclone.org) (MIT) sebagai proses terpisah. WebDAV dan Local / NAS folder **tidak** membutuhkan rclone.

**Disertakan di semua paket.** `CloudSBiian-Setup.exe`, `CloudSBiian-Portable-x64.zip`, dan `CloudSBiian.exe` single-file berisi `rclone\rclone.exe` resmi (+ `rclone\COPYING.txt`). Saat packaging, arsip rclone diunduh dari `downloads.rclone.org` dan dicocokkan dengan `SHA256SUMS` rilis tersebut; build gagal bila checksum tidak cocok. Exe single-file mengekstrak rclone bersama komponen lain ke folder ekstraksi .NET saat dijalankan.

**Urutan pencarian** (panel rclone → **Details** menampilkan semua lokasi yang diperiksa):
1. Path di **Settings → rclone** (bila diisi dan ada; bila diisi tapi tidak ada, hal itu dilaporkan dan pencarian lanjut).
2. Salinan bawaan CloudSBiian: `<folder aplikasi>\rclone\rclone.exe`, `<folder aplikasi>\rclone.exe`, dan folder `CloudSBiian.exe` itu sendiri.
3. Salinan hasil **Download rclone**: `%LOCALAPPDATA%\CloudSBiian\tools\rclone\rclone.exe` (portable: `data\tools\rclone`).
4. CloudSBiian terinstal (`InstallLocation` dari registry uninstall) dan `Program Files\CloudSBiian\rclone`.
5. `Program Files\rclone`, winget (`%LOCALAPPDATA%\Microsoft\WinGet\Links` dan `Packages\Rclone.Rclone*`), Scoop (`~\scoop\apps\rclone\current`, `~\scoop\shims`), Chocolatey (`ProgramData\chocolatey\lib\rclone.portable\tools\rclone-*`, `ProgramData\chocolatey\bin`).
6. PATH proses dan PATH user/mesin terbaru dari registry (rclone yang baru dipasang terdeteksi tanpa logout).

**Pemeriksaan dependensi** (saat start, tombol **Re-check**, setelah Download/Browse, dan saat path di Settings berubah): `rclone version` harus berjalan dan ≥ **v1.60**, daemon `rclone rcd` harus start, lalu daftar backend dibaca (`config/providers`). Salinan yang rusak/terlalu lama dilewati dan salinan berikutnya dicoba; path dari Settings dipakai secara eksklusif. Hasilnya: `Ready`, `Missing`, `Unusable`, atau `StartFailed`, dengan alasan yang ditampilkan di panel rclone (halaman Cloud Accounts, Settings, dan dialog Add Account) dan di Logs → Dependency check.

**Status provider** berasal dari pemeriksaan itu: selama rclone belum `Ready`, provider berbasis rclone *Blocked* dengan alasan sebenarnya (tidak terpasang / terlalu lama / gagal start). Setelah `Ready`, setiap provider dicek terhadap daftar backend rclone yang berjalan; backend yang tidak ada tetap *Blocked*. Status tidak pernah menjadi *Supported* hanya karena rclone ditemukan.

**Jika rclone tidak tersedia** (mis. dihapus antivirus):
- **Download rclone** — unduh rilis resmi terbaru untuk Windows dari `downloads.rclone.org` (±25 MB), verifikasi SHA-256 terhadap `SHA256SUMS`, cek `rclone version`, lalu simpan di folder tools di atas. Tidak ada data akun yang dikirim.
- **Browse for rclone.exe…** — pilih `rclone.exe` yang sudah ada; versinya dicek sebelum disimpan ke Settings.
- **Re-check** — jalankan pemeriksaan ulang (mis. setelah memasang rclone dengan winget: `winget install Rclone.Rclone`).

Akun yang sudah ditambahkan terhubung otomatis setelah rclone menjadi `Ready`. Form opsi dibuat dari skema yang dilaporkan rclone sendiri (`config/providers`), jadi selalu sesuai versi rclone yang dipakai.

## Google Drive / OneDrive / Dropbox (OAuth)

1. Pilih provider, isi nama, klik **Add account**.
2. Browser terbuka pada halaman login resmi provider (`rclone authorize`). Login dan setujui akses.
3. Token tersimpan terenkripsi (DPAPI). CloudSBiian tidak pernah melihat password Anda.
4. Opsi lanjutan: client ID/secret milik Anda sendiri untuk menghindari rate limit bersama.

Bila sesi kedaluwarsa, akun berstatus `AuthenticationExpired`; tekan **Reconnect** untuk login ulang akun itu saja.

## WebDAV (native, Supported)

URL server (mis. Nextcloud `https://host/remote.php/dav/files/<user>/`), user, password. Range read, PUT, MOVE, MKCOL, DELETE, dan kuota RFC 4331 (bila server mendukung).

## SFTP (rclone `sftp`)

`host`, `port`, `user`, dan `pass` atau `key_file`. Opsi `shell_type = none` dan `disable_hashcheck = true` berguna untuk server yang tidak mengizinkan perintah shell.

## S3-compatible (rclone `s3`)

`provider` (AWS, Minio, Wasabi, Cloudflare, Other…), `access_key_id`, `secret_access_key`, `region`/`endpoint`. Gunakan **Root folder** `/nama-bucket` untuk satu bucket.

## Local / NAS (native, Supported)

Folder lokal atau share jaringan `\\nas\share` yang sudah dapat diakses Windows.

## SMB (rclone `smb`)

`host`, `user`, `pass`, `domain`. Belum diuji otomatis — untuk NAS, provider Local/NAS biasanya lebih sederhana.

## TeraBox

**Fakta yang perlu Anda ketahui:**
- Tidak ditemukan API publik TeraBox yang terdokumentasi dan dapat diverifikasi untuk aplikasi pihak ketiga.
- rclone resmi **tidak** memiliki backend `terabox`. Ada fork rclone tidak resmi (mis. turunan yang menambahkan backend `terabox`) yang memakai sesi web TeraBox (cookie) — ini bisa rusak kapan saja bila TeraBox mengubah situsnya, dan memakai akun dengan cara yang mungkin tidak didukung ketentuan TeraBox. Gunakan dengan risiko sendiri.

**Langkah (opsional, eksperimental):**
1. Dapatkan build rclone yang menyertakan backend `terabox` dari sumber yang Anda percayai.
2. **Settings → rclone → Browse…** pilih `rclone.exe` tersebut → **Save** (atau **Browse for rclone.exe…** di panel rclone). Status TeraBox berubah dari *Blocked* menjadi *Experimental* hanya jika daftar backend rclone tersebut benar-benar berisi `terabox`. rclone resmi (yang disertakan dan yang diunduh oleh **Download rclone**) tidak memilikinya, jadi TeraBox tetap *Blocked* dengan rclone resmi.
3. **Cloud Accounts → + Add TeraBox Account**. Form menampilkan opsi yang diminta backend tersebut (misalnya cookie sesi yang Anda salin sendiri dari browser Anda). CloudSBiian tidak mengambil cookie secara otomatis.
4. Ulangi untuk akun TeraBox lain. Setiap akun: ID, remote rclone (`csb_<id>`), kredensial, cache, staging, antrean, dan status error terpisah. Akun-akun dapat terhubung bersamaan karena setiap akun adalah remote rclone tersendiri.
5. **Remove** meminta konfirmasi, menghentikan transfer akun itu, membersihkan cache akun itu, dan (opsional) menghapus kredensial. File di TeraBox tidak dihapus. Akun lain tidak terpengaruh.

Kemampuan yang tersedia (list, baca Range, upload, rename, delete, kuota) sepenuhnya bergantung pada backend fork. CloudSBiian mendeteksi saat backend mengabaikan Range dan beralih ke unduhan berurutan.

## Other rclone backend

Semua backend yang dilaporkan rclone (pCloud, Box, Mega, …) dapat dicoba. Status: Experimental.
