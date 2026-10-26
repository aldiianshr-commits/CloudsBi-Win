# Keamanan

## Rahasia (password, token, cookie, secret key)

- Disimpan dengan **Windows DPAPI** (scope CurrentUser) di `%LOCALAPPDATA%\CloudSBiian\secrets\<id-akun>.secret`, satu file per akun. ID akun dicampurkan ke entropi DPAPI, sehingga blob akun A yang disalin ke file akun B tidak bisa didekripsi (diuji: `SecretTests.Dpapi_BlobOfOneAccountCannotBeReadAsAnother`).
- Saat akun dihapus dengan opsi "hapus kredensial", file ditimpa nol lalu dihapus.
- Rahasia tidak pernah ditulis ke `settings.json`, database SQLite, atau file konfigurasi rclone. rclone berjalan dengan konfigurasi **in-memory** (`--config ""`); rahasia dikirim lewat API RC lokal saat akun terhubung.
- UI tidak menampilkan rahasia; field rahasia memakai PasswordBox. Identitas (e-mail) ditampilkan dalam bentuk tersamar.

## Log dan pesan error

- Setiap nilai rahasia yang dimuat didaftarkan ke `SecretRedactor` dan diganti `***` di semua log, pesan error, dan ekspor diagnostik.
- Pola tambahan dimask: `token=`, `password=`, `access_token`, `cookie`, `Authorization: Bearer/Basic`, `--rc-pass`.
- Ekspor diagnostik (Logs & Diagnostics) berisi laporan dependensi, settings, riwayat aktivitas, dan log yang sudah diredaksi — tetap periksa sebelum dibagikan.

## Jaringan dan proses

- Daemon rclone hanya listen di `127.0.0.1` pada port acak, dengan user/password acak 192-bit yang diberikan lewat variabel lingkungan. Proses rclone diikat ke Job Object (ikut berhenti bila CloudSBiian crash).
- Login OAuth (Google Drive/OneDrive/Dropbox) dilakukan di browser Anda pada halaman resmi provider melalui `rclone authorize`; CloudSBiian tidak pernah melihat password akun cloud.
- CloudSBiian tidak mengirim kredensial ke server selain provider (langsung atau melalui proses rclone lokal). Tidak ada telemetri.
- **Download rclone** hanya menghubungi `downloads.rclone.org` (HTTPS) untuk `version.txt`, `SHA256SUMS`, dan arsip Windows resmi; tidak ada data akun yang dikirim. Arsip ditolak bila SHA-256 tidak cocok dengan `SHA256SUMS`, dan exe hanya dipasang bila `rclone version` berjalan dan melaporkan versi yang diunduh. Batasan: tanda tangan PGP `SHA256SUMS` belum diverifikasi (checksum dan arsip berasal dari origin HTTPS yang sama), jadi perlindungannya terhadap unduhan rusak/terpotong dan mirror yang salah, bukan terhadap kompromi server rclone itu sendiri.
- Layanan eksternal yang dihubungi: endpoint provider yang Anda tambahkan; `winfsp.dev` hanya jika Anda menekan "Install WinFsp" tanpa MSI bawaan.

## Sistem file

- Semua path dinormalisasi; `..` ditolak (`PathValidationException`), nama perangkat Windows (CON, NUL, …) dan karakter tidak valid ditolak, path lokal dipastikan tetap di dalam root (`CloudPath.ToLocalPath`).
- Penghapusan dari drive/File Browser menghapus di cloud (sesuai harapan pengguna) dengan konfirmasi di File Browser; "Clear cache", hapus akun, dan uninstall **tidak pernah** menghapus data cloud.
- Aplikasi berjalan tanpa hak admin (`asInvoker`). Installer butuh admin karena WinFsp adalah driver kernel.

## Melaporkan kerentanan

Buka issue privat (Security advisory) di repositori GitHub. Jangan menyertakan token atau password dalam laporan.
