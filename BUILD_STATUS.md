# Build status

## Pipeline

`.github/workflows/ci.yml` pada setiap push ke `main` (runner `windows-latest`, .NET SDK 10.0.x):

1. `dotnet build CloudSBiian.slnx -c Release` — 10 proyek (8 aplikasi/pustaka + 2 tes).
2. Unit test (mock).
3. Pasang WinFsp + rclone (Chocolatey) → integration test (driver WinFsp nyata, server rclone lokal).
4. `build/package.ps1`: unduh rclone resmi (cek SHA-256 terhadap `SHA256SUMS`, build gagal bila tidak cocok) + MSI WinFsp, publish self-contained win-x64 dengan rclone sebagai konten, exe single-file (rclone tertanam, dikompresi), zip portable, installer Inno Setup.
5. UI self-test pada paket portable (`--ui-selftest`), termasuk simulasi "rclone tidak ada" lalu Re-check; CI gagal bila aplikasi tidak memakai rclone bawaannya.
6. Installer: install diam → self-test aplikasi terinstal (harus memakai rclone bawaan) → uninstall diam (cek data pengguna tetap) → self-test exe single-file (harus memakai rclone tertanam).
7. Artefak diunggah ke run dan, bila semua langkah lulus, ke pre-release bergulir **`latest-build`**.
8. Ringkasan + log ke branch `ci-logs`.

## Status terakhir yang terverifikasi (build 0.1.20, commit `7d0015e`)

| Langkah | Status | Bukti |
|---|---|---|
| Build Release (10 proyek) | ✅ | `ci/build.log` |
| Unit test 102/102 | ✅ | `ci/summary.txt` |
| Integration test 16/16 (WinFsp 2.1, rclone, unduhan rclone resmi nyata) | ✅ | `ci/integration.log` |
| Paket: `CloudSBiian.exe` (single-file, rclone tertanam, 91 957 403 byte), `CloudSBiian-Portable-x64.zip` (94 835 055 byte), `CloudSBiian-Setup.exe` (68 113 913 byte) | ✅ | `ci/package.log`, release `latest-build` |
| UI self-test (35 pemeriksaan: 11 halaman × 2 tema, 10 dialog Add Account, simulasi rclone hilang + Re-check) | ✅ | `ci/ui-selftest.txt` |
| Portable/terinstal/single-file memakai rclone bawaan: `rclone: Ready \| bundled with CloudSBiian \| v1.75.2 \| … \| backends 69` | ✅ | `ci/ui-selftest*.txt`, `ci/install-test.txt` |
| Install diam → self-test terinstal → uninstall diam (data pengguna tetap) → self-test single-file | ✅ | `ci/install-test.txt` |

## Batasan build

- Tidak ada code signing (SmartScreen dapat memperingatkan).
- Lingkungan pengembangan agen tidak memiliki .NET SDK; semua kompilasi, tes, dan packaging dilakukan di CI Windows. Tidak ada artefak yang diklaim tanpa dibuat oleh pipeline tersebut.
- Versi: `0.1.<run_number>`.
