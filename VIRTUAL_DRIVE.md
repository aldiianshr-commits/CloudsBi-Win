# Virtual drive

## Dependensi

CloudSBiian memakai [WinFsp](https://winfsp.dev) (driver file system user-mode, GPLv3 + FLOSS exception) melalui binding `winfsp.net` 2.0 — kompatibel dengan WinFsp **2.0 atau lebih baru**. Halaman *Virtual Drive* memeriksa registry `HKLM\SOFTWARE\WOW6432Node\WinFsp\InstallDir` dan versi `winfsp-x64.dll`; tombol **Install WinFsp** menjalankan MSI bawaan (butuh admin) atau membuka halaman unduhan resmi. Tanpa WinFsp, mount tidak mungkin dan aplikasi mengatakannya dengan jelas.

## Mount

- Pilih huruf yang tersedia (huruf yang sudah dipakai Windows tidak ditawarkan; dicek lagi saat mount).
- **All accounts**: root drive berisi satu folder per akun (nama tampilan, karakter tidak valid diganti `_`, duplikat diberi "(2)").
- **Satu akun**: isi akun langsung di root.
- Opsi: label volume, read-only, tampil sebagai network drive (prefix UNC), mount otomatis saat start.
- Unmount memperingatkan bila masih ada handle terbuka. Upload tertunda tetap berjalan setelah unmount.

## Operasi

| Operasi | Perilaku |
|---|---|
| List / stat | Dari listing ter-cache (TTL), dilengkapi file lokal yang belum terunggah. Nama tidak valid untuk Windows disembunyikan. |
| Baca | Lewat cache blok, P0. Random access dengan Range. |
| Buat / tulis | Ke staging lokal; diunggah setelah handle terakhir ditutup. File yang sudah ada diunduh lengkap sebelum modifikasi pertama. |
| Rename/move (akun sama) | Rename di server (file yang belum terunggah: hanya lokal). Huruf besar/kecil yang dikirim Windows dipetakan ke ejaan sebenarnya di cloud. |
| Move antar akun | Windows melakukan salin + hapus. |
| Delete | File: hapus di cloud (dan batalkan upload tertunda). Folder: hanya bila kosong. Root akun tidak dapat dihapus. |
| Volume size | Jumlah kuota yang dilaporkan provider; jika tidak ada, nilai placeholder dari Settings. |

Provider tanpa izin upload, atau drive read-only, mengembalikan *media write protected*.

## Ketahanan

- Setiap callback WinFsp dibungkus: error backend menjadi kode NTSTATUS (mis. `LOGON_FAILURE` untuk sesi kedaluwarsa, `UNEXPECTED_NETWORK_ERROR` saat offline) — tidak ada exception yang lolos ke driver.
- Setiap operasi backend dibatasi waktu (5 menit; blok 3 menit; listing 2 menit).
- Pembacaan blok yang gagal sementara dicoba ulang 3× dengan backoff; kegagalan auth menandai hanya akun terkait sebagai kedaluwarsa.
- Saat sleep/resume dan perubahan jaringan, metadata diinvalidasi, koneksi divalidasi ulang, dan upload offline dicoba lagi.
- Saat aplikasi keluar semua drive di-unmount sebelum layanan lain dihentikan; handle staging ditutup secara berurutan.

## Diuji (integrasi, WinFsp asli)

Mount/browse/baca berurutan & acak/unmount, 8 pembaca paralel, tulis–overwrite–rename–mkdir–delete via `System.IO` sampai ke backend, huruf terpakai ditolak, isolasi folder multi-akun + move antar akun, mount read-only, putus/sambung koneksi, benchmark. Lihat [TEST_REPORT.md](TEST_REPORT.md).
