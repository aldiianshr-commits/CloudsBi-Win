# Game Library & launcher

CloudSBiian **bukan** layanan game streaming: game berjalan di PC Anda; hanya file-nya yang berada di cloud.

## Menambah entri

- **+ Add Game or Application**: pilih executable. Jika berada di drive CloudSBiian, entri dihubungkan ke akun dan folder cloud (mode awal *Hybrid Optimized*). Executable di disk lokal menjadi aplikasi lokal.
- **Scan Folder**: mencari `.exe` sampai 3 level (melewati uninstaller, crash reporter, redistributable) lalu Anda memilih yang ditambahkan.
- Edit: nama, argumen peluncuran (satu baris), working directory (relatif ke folder game atau absolut), mode, prioritas cache (0 normal, 1 pin saat dipersiapkan, 2 selalu di-pin).

## Mode

| Mode | Cara kerja | Catatan |
|---|---|---|
| Cloud Direct | Menjalankan dari virtual drive; data diunduh saat dibaca. | Performa tergantung jaringan, Range, ukuran file, dan pola baca game. |
| Hybrid Optimized | Sama, ditambah prefetch blok yang terekam pada peluncuran sebelumnya dan pinning. | Peluncuran pertama merekam trace (10 menit pertama). |
| Full Local | Menyalin folder ke disk lokal, memverifikasi setiap file (ukuran + SHA-256 data diterima vs. data di disk), lalu menjalankan dari lokal. | Untuk game dengan anti-cheat/launcher atau performa disk tinggi. File identik dilewati saat instal ulang. |

## Optimize & Prepare

Memeriksa: argumen, koneksi & kredensial akun, keberadaan executable di cloud, ketersediaan drive / salinan lokal, profil akses (blok yang sudah/belum di-cache), ruang cache dan disk, upload tertunda/konflik, petunjuk kompatibilitas. Tidak mengunduh apa pun sampai Anda memilih **Prefetch recorded data** (konfirmasi tambahan di atas 2 GB) atau **Install locally…**. Semua operasi bisa dibatalkan; data yang sudah di-cache/disalin dipertahankan. Estimasi waktu hanya muncul bila ada kecepatan terukur dan diberi label "approximate".

## Launch / Stop

- Launch memakai `CreateProcess` biasa (seperti dari Explorer). Status, PID, waktu mulai disimpan; exit code dicatat.
- Stop: minta jendela utama menutup, lalu menghentikan process tree setelah 10 detik (dengan konfirmasi).
- Setelah CloudSBiian restart: proses yang masih berjalan (PID + waktu mulai cocok) dilacak kembali; yang sudah berhenti ditandai.

## Kompatibilitas

Pemindai mencari penanda EasyAntiCheat, BattlEye, EA Anti-Cheat, Steamworks, Epic Online Services, Ubisoft Connect, EA activation, redistributable VC++/DirectX dan memberi saran (biasanya: Full Local / jalankan launcher resmi). Tidak ada dukungan universal untuk Steam, EA app, Epic Games, Ubisoft; tidak ada upaya melewati DRM atau anti-cheat.
