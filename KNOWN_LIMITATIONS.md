# Batasan yang diketahui

Daftar ini sengaja ditulis lengkap. Jika sesuatu tidak ada di sini dan tidak tercantum di [TEST_REPORT.md](TEST_REPORT.md), anggap belum diverifikasi.

## Provider

- **TeraBox**: tidak ada API publik TeraBox yang dapat diverifikasi untuk aplikasi pihak ketiga (halaman "integrations" TeraBox tidak dapat dibaca/diverifikasi saat pengembangan; tidak ada dokumentasi endpoint yang bisa dipastikan). rclone resmi tidak memiliki backend `terabox`. CloudSBiian **tidak** membuat endpoint TeraBox sendiri, tidak mengambil cookie dari browser secara otomatis, dan tidak melewati CAPTCHA/MFA. TeraBox hanya dapat dipakai dengan build rclone yang menyertakan backend `terabox` (fork tidak resmi) yang Anda pilih sendiri di Settings; data login (mis. cookie) Anda isi sendiri di form. Status: **Blocked** tanpa backend tersebut, **Experimental** dengannya. Kemampuan Range, kuota, dan multi-sesi bergantung sepenuhnya pada backend fork itu dan **belum diuji** oleh proyek ini terhadap TeraBox asli.
- **Google Drive, OneDrive, Dropbox**: memakai backend resmi rclone (API resmi). Belum diuji dengan akun asli di CI (butuh login interaktif). Client ID bawaan rclone dibagi banyak pengguna sehingga bisa terkena rate limit; Anda dapat memasukkan client ID/secret sendiri pada opsi lanjutan.
- **OneDrive**: rclone dapat merotasi refresh token; CloudSBiian menyimpan token terbaru setiap 10 menit, saat sleep, saat disconnect dan saat keluar. Jika aplikasi crash tepat setelah rotasi, Reconnect mungkin diperlukan.
- **Identitas akun** (e-mail) tidak dilaporkan oleh adapter rclone; ditampilkan "not reported".
- **Kuota**: hanya ditampilkan jika backend melaporkannya. Bila tidak, Windows menampilkan kapasitas placeholder dari Settings (default 1 TB) — angka ini bukan kuota sebenarnya.
- **rclone**: wajib untuk semua provider kecuali WebDAV dan Local/NAS. Disertakan di semua paket; bila hilang, aplikasi dapat mengunduh rilis resmi (butuh akses internet ke `downloads.rclone.org`). Tanda tangan PGP `SHA256SUMS` tidak diverifikasi (lihat SECURITY.md). Unduhan dalam aplikasi selalu mengambil rilis stabil terbaru; versi tidak di-pin. Versi minimum yang diterima: v1.60.
- **SMB via rclone**: belum ada tes otomatis. Untuk NAS yang sudah dapat diakses Windows, gunakan provider "Local / NAS folder".

## Virtual drive

- Butuh **WinFsp ≥ 2.0** (driver kernel, instalasi butuh admin).
- Timestamp dan atribut file yang diubah aplikasi (SetBasicInfo) diterima tetapi tidak dikirim ke cloud; backend memakai waktu modifikasinya sendiri.
- Nama file yang tidak valid di Windows (mis. berisi `:`), serta nama yang hanya berbeda huruf besar/kecil di backend yang case-sensitive, tidak bisa ditampilkan bersamaan; nama tidak valid disembunyikan.
- Memindahkan file **antar akun** dilakukan Windows sebagai salin + hapus (bukan rename di server).
- Hard link, alternate data stream, extended attributes, reparse point, ACL per file: tidak didukung. Semua file memiliki security descriptor yang sama.
- Menulis bagian kecil dari file besar yang belum ter-cache mengunduh **seluruh** file terlebih dahulu (diperlukan agar tulisan parsial benar).
- Operasi metadata menunggu backend; folder besar pada backend lambat dapat membuat Explorer menunggu. Listing di-cache 30 detik (dapat diatur).
- Sleep/resume: koneksi divalidasi ulang setelah resume dan antrean upload dicoba lagi; ini **tidak** diuji dengan siklus sleep sungguhan di CI (hanya simulasi putus/sambung jaringan).

## Cache dan upload

- Anggaran cache adalah target; batas ruang kosong minimum selalu menang. Jika keduanya tidak bisa dipenuhi, data dibaca tanpa disimpan di cache.
- Staging (perubahan yang belum terunggah) tidak dihitung dalam anggaran cache dan **tidak pernah** di-evict.
- Upload dimulai setelah handle terakhir ditutup. File yang terus terbuka (mis. database aplikasi) baru diunggah setelah ditutup.
- Deteksi konflik berbasis identitas (ukuran + waktu modifikasi + ID/ETag bila ada). Backend yang tidak memberi waktu modifikasi presisi dapat menghasilkan konflik palsu atau melewatkan perubahan yang identik ukuran/waktunya.
- Mode write-through menunggu upload maksimal 10 menit saat file ditutup; setelah itu data tetap di antrean.

## Prediction engine

- Heuristik, bukan machine learning. Tidak ada klaim peningkatan performa selain angka yang terukur di [BENCHMARKS.md](BENCHMARKS.md).
- Trace peluncuran merekam blok yang dibaca selama 10 menit pertama setiap peluncuran Cloud Direct/Hybrid.

## Game library

- Tidak ada dukungan "universal" untuk Steam/Epic/EA/Ubisoft. Game yang butuh launcher, DRM online, atau anti-cheat (EAC, BattlEye, EA AC) kemungkinan besar tidak berjalan dari virtual drive; gunakan Full Local atau instal lewat launcher resminya. CloudSBiian tidak melewati DRM/anti-cheat.
- Pemindai kompatibilitas hanya mencari nama file/folder yang dikenal; tidak adanya penanda bukan jaminan kompatibel.
- Estimasi waktu hanya ditampilkan jika ada kecepatan terukur untuk akun tersebut, dan selalu ditandai sebagai perkiraan.

## Platform & pengujian

- CI berjalan di Windows Server 2025 (runner GitHub `windows-latest`). Windows 10/11 desktop tidak diuji secara langsung; API yang dipakai tersedia sejak Windows 10 1809.
- Tidak ada penandatanganan kode (code signing): SmartScreen dapat memperingatkan saat menjalankan installer.
- Antarmuka hanya berbahasa Inggris.

---

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winfsp (dipakai di bawah GPLv3 FLOSS exception; lihat [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
