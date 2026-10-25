# Build status

## Pipeline

`.github/workflows/ci.yml` pada setiap push ke `main` (runner `windows-latest`, .NET SDK 10.0.x):

1. `dotnet build CloudSBiian.slnx -c Release` — 10 proyek (8 aplikasi/pustaka + 2 tes).
2. Unit test (mock).
3. Pasang WinFsp + rclone (Chocolatey) → integration test (driver WinFsp nyata, server rclone lokal).
4. `build/package.ps1`: publish self-contained win-x64, exe single-file, zip portable (dengan rclone & WinFsp MSI resmi), installer Inno Setup.
5. UI self-test pada paket portable (`--ui-selftest`).
6. Installer: install diam → self-test aplikasi terinstal → uninstall diam (cek data pengguna tetap) → self-test exe single-file.
7. Artefak diunggah ke run dan, bila semua langkah lulus, ke pre-release bergulir **`latest-build`**.
8. Ringkasan + log ke branch `ci-logs`.

## Status terakhir yang terverifikasi (build 0.1.17, commit `3e5f11e`)

| Langkah | Status | Bukti |
|---|---|---|
| Build Release (10 proyek) | ✅ | `ci/build.log` |
| Unit test 83/83 | ✅ | `ci/summary.txt` |
| Integration test 14/14 (WinFsp 2.1, rclone) | ✅ | `ci/integration.log` |
| Paket: `CloudSBiian.exe` (single-file, ~128 MB), `CloudSBiian-Portable-x64.zip` (~90 MB), `CloudSBiian-Setup.exe` (~65 MB) | ✅ | `ci/package.log`, release `latest-build` |
| UI self-test (32 layar) | ✅ | `ci/ui-selftest.txt` |
| Install diam → self-test terinstal → uninstall diam (data pengguna tetap) → self-test single-file | ✅ | `ci/install-test.txt` |

## Artefak

| File | Keterangan |
|---|---|
| `CloudSBiian.exe` | Self-contained single-file (WPF + .NET 10 + SQLite native). rclone tidak disertakan. |
| `CloudSBiian-Portable-x64.zip` | Folder aplikasi, `portable.flag`, `rclone\rclone.exe`, `dependencies\winfsp.msi`, README-PORTABLE.txt. |
| `CloudSBiian-Setup.exe` | Installer admin: aplikasi ke Program Files, rclone, MSI WinFsp (task "Install WinFsp" hanya muncul bila belum terpasang), shortcut. Uninstall tidak menghapus data pengguna, data cloud, atau WinFsp. |

## Batasan build

- Tidak ada code signing (SmartScreen dapat memperingatkan).
- Lingkungan pengembangan agen tidak memiliki .NET SDK; semua kompilasi, tes, dan packaging dilakukan di CI Windows. Tidak ada artefak yang diklaim tanpa dibuat oleh pipeline tersebut.
- Versi: `0.1.<run_number>`.
