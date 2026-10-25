# Cache engine

## Penyimpanan

- **Blok** (default 4 MiB, dapat diatur 1–64 MiB) disimpan sebagai file di `cache\blocks\<akun>\chunks\<xx>\<kunci>_<indeks>.bin`. Kunci = 128 bit pertama SHA-256(ID akun + path).
- Metadata di SQLite: `cache_files` (path, identitas versi, ukuran blok), `cache_chunks` (panjang, byte disk, SHA-256, waktu akses, asal demand/prefetch, sudah dipakai).
- Penulisan: file sementara → rename (atomik); kemudian baris SQLite. File sementara sisa crash dihapus saat start.
- **Cache RAM** (96 MB) menyimpan blok terverifikasi yang baru dipakai sehingga pembacaan kecil berulang tidak menyentuh disk.

## Konsistensi

- **Read-through**: miss → unduh blok (HTTP Range) → simpan → kembalikan.
- **Identitas versi** = ukuran + waktu modifikasi + ID/ETag. Jika listing menunjukkan identitas berbeda, semua blok versi lama dibuang sebelum dibaca.
- **Verifikasi**: SHA-256 dicek saat blok dibaca dari disk (dapat dimatikan). Blok rusak/hilang dibuang dan diunduh ulang (`cache.corrupt_chunks`). "Verify integrity" memeriksa semua blok dan menghapus file yatim.
- **Metadata & listing** di-cache di RAM (TTL default 30 s) dan disimpan ke SQLite agar folder tetap bisa dijelajahi saat offline (ditandai *stale*).

## Anggaran & eviction

- Pilihan 10/20/30/40 GB atau custom; plus **ruang kosong minimum** disk.
- Pemakaian dihitung dari **byte fisik** (dibulatkan ke cluster 4 KiB), bukan ukuran file di cloud.
- Sebelum menulis blok: jika melewati anggaran atau batas ruang kosong, blok LRU yang **tidak di-pin** dihapus (plus 5% cadangan). Jika tetap tidak cukup, data dikembalikan tanpa disimpan (pembacaan tetap berhasil).
- **Pin** (file atau folder) tidak pernah di-evict. "Cache now" pada Cache Manager mengunduh isi pin.
- **Pengecualian** glob (`*.tmp`, `/Videos/**`, global atau per akun): dibaca langsung, tidak di-cache, tidak di-prefetch.

## Write-back & status upload

| Status | Arti |
|---|---|
| `PendingUpload` | Perubahan lokal menunggu (atau menunggu jadwal retry). |
| `Uploading` | Sedang diunggah. Setelah crash/restart kembali ke `PendingUpload`. |
| `Synced` | Berhasil; staging dihapus, cache & metadata diperbarui. |
| `RetryRequired` | Batas percobaan tercapai, sesi kedaluwarsa, izin/kuota habis. Butuh tindakan (Retry). |
| `Conflict` | File di cloud berubah sejak dibuka di sini. Tidak ditimpa. Pilih: *Keep mine*, *Keep both* (unggah sebagai "nama (conflict …)"), *Use cloud version*. |
| `Offline` | Jaringan/akun tidak tersedia; dicoba otomatis dengan backoff dan saat jaringan kembali. |

Data staging tidak pernah dihapus oleh eviction, "Clear cache", atau uninstall. Data yang ditulis tetapi belum masuk antrean saat crash dipulihkan dari file sidecar saat start.

## Mode tulis

- **Write-back** (default): file ditutup segera; upload di latar belakang.
- **Write-through on close**: menutup file menunggu upload (maks. 10 menit); jika gagal, data tetap di antrean.

## Prioritas unduhan

Per akun ada pembatas konkurensi (default 4). Satu slot selalu dicadangkan untuk P0/P1; P2/P3 menunggu bila ada permintaan aktif; permintaan P0 yang menunggu membatalkan prefetch berprioritas terendah. Prefetch dibatasi bandwidth (default 8 MB/s, 0 = tanpa batas).
