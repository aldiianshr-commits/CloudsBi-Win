# Menyiapkan provider

Semua akun ditambahkan di **Cloud Accounts → + Add Account** (atau **+ Add TeraBox Account**). Ulangi untuk menambah akun berikutnya dari provider yang sama — setiap akun independen.

Kolom umum:
- **Display name** — nama di aplikasi dan nama folder di drive (mode "All accounts").
- **Root folder** — hanya folder ini yang ditampilkan (default `/`).

## rclone

Provider Google Drive, OneDrive, Dropbox, SFTP, S3, SMB, TeraBox, dan "Other rclone backend" memakai [rclone](https://rclone.org) sebagai proses terpisah.
- Installer dan paket portable menyertakan `rclone\rclone.exe` (build resmi).
- `CloudSBiian.exe` single-file memakai rclone dari PATH atau path yang Anda pilih di **Settings → rclone**.
- Form opsi dibuat dari skema yang dilaporkan rclone sendiri (`config/providers`), jadi selalu sesuai versi rclone yang dipakai.

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
2. **Settings → rclone → Browse…** pilih `rclone.exe` tersebut → **Save**. Status TeraBox berubah dari *Blocked* menjadi *Experimental* jika backend `terabox` terdeteksi.
3. **Cloud Accounts → + Add TeraBox Account**. Form menampilkan opsi yang diminta backend tersebut (misalnya cookie sesi yang Anda salin sendiri dari browser Anda). CloudSBiian tidak mengambil cookie secara otomatis.
4. Ulangi untuk akun TeraBox lain. Setiap akun: ID, remote rclone (`csb_<id>`), kredensial, cache, staging, antrean, dan status error terpisah. Akun-akun dapat terhubung bersamaan karena setiap akun adalah remote rclone tersendiri.
5. **Remove** meminta konfirmasi, menghentikan transfer akun itu, membersihkan cache akun itu, dan (opsional) menghapus kredensial. File di TeraBox tidak dihapus. Akun lain tidak terpengaruh.

Kemampuan yang tersedia (list, baca Range, upload, rename, delete, kuota) sepenuhnya bergantung pada backend fork. CloudSBiian mendeteksi saat backend mengabaikan Range dan beralih ke unduhan berurutan.

## Other rclone backend

Semua backend yang dilaporkan rclone (pCloud, Box, Mega, …) dapat dicoba. Status: Experimental.
