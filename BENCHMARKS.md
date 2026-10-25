# Benchmarks

Semua angka di sini **diukur** oleh tes otomatis `WinFspDriveTests.Benchmark_ColdAndWarmSequentialRead` di GitHub Actions (`windows-latest`, Windows Server 2025, VM runner standar), dan dipublikasikan sebagai `ci/bench-results.json` di branch `ci-logs`. Tidak ada angka yang diperkirakan.

## Apa yang diukur

File acak 64 MiB di backend **in-memory** (`InMemoryProvider`), dibaca melalui drive WinFsp yang di-mount dengan `File.ReadAllBytes` (berurutan), lalu 500 pembacaan acak 4 KiB dengan `FileStream`. Blok cache 1 MiB (nilai tes; default aplikasi 4 MiB).

Karena backend berada di memori, ini mengukur **overhead tumpukan CloudSBiian** (WinFsp ↔ .NET ↔ cache blok ↔ SQLite ↔ disk), **bukan** kecepatan jaringan atau provider cloud mana pun. Dengan cloud sungguhan, pembacaan *cold* dibatasi oleh bandwidth dan latensi provider.

## Hasil

| Metrik | Sebelum cache RAM (build 0.1.13) | Saat ini (build 0.1.14) |
|---|---|---|
| Baca berurutan 64 MiB, cold (miss → unduh blok → simpan) | 22.7 MB/s | **157.5 MB/s** |
| Baca berurutan 64 MiB, warm (semua blok ter-cache) | 537 MB/s | **1493.5 MB/s** |
| Baca acak 4 KiB warm, p50 | 1.086 ms | **0.046 ms** |
| Baca acak 4 KiB warm, p95 | 1.254 ms | **0.075 ms** |
| Cache hit rate setelah dua pembacaan penuh | 0.50 | 0.50 (pembacaan pertama semua miss, kedua semua hit) |

Perubahan antara dua build: cache RAM 96 MB untuk blok terverifikasi, cache record file di memori, dan penulisan blok tanpa `FILE_FLAG_WRITE_THROUGH` (keamanan crash tetap lewat temp-file + rename + SHA-256). Angka build 0.1.12 (sebelumnya) bervariasi antara 22–51 MB/s cold di runner yang sama, sehingga variasi VM ±2× harus diperhitungkan.

Catatan: hasil "warm" untuk file ≤ 96 MB sebagian besar dilayani dari cache RAM; file yang lebih besar dari cache RAM dibaca dari blok di disk (dengan verifikasi SHA-256).

## Yang belum diukur

- Throughput terhadap Google Drive/OneDrive/Dropbox/TeraBox nyata (butuh akun; tidak dijalankan di CI).
- Efek prefetch pada waktu muat game nyata.
- Kinerja di Windows 10/11 desktop dengan SSD/HDD konsumen.

Pengguna dapat melihat metrik terukur mereka sendiri di halaman **Monitoring** dan **Dashboard** (kecepatan aktual, hit rate, latensi p50/p95, akurasi prefetch).
