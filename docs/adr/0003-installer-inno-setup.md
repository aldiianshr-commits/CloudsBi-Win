# ADR-0003: Installer memakai Inno Setup

- Status: **Accepted** (mendokumentasikan keputusan yang sudah diimplementasikan; dicatat 2026-10-26)
- Kode: `installer/CloudSBiian.iss`, `build/package.ps1`

## Keputusan

`CloudSBiian-Setup.exe` dibuat dengan Inno Setup, bukan WiX.

## Alasan

- Satu skrip `.iss` bisa memeriksa WinFsp di registry (`HKLM32\SOFTWARE\WinFsp\InstallDir`) dan menampilkan task "Install the WinFsp file system driver" yang **terlihat** oleh pengguna hanya bila WinFsp belum terpasang, lalu menjalankan MSI resmi tanpa modifikasi (`msiexec /i winfsp.msi`). Ini memenuhi syarat FLOSS exception WinFsp (installer resmi, tidak diubah) dan transparan.
- Exe setup tunggal lebih mudah diunduh daripada MSI + bootstrapper WiX Burn.
- Bisa di-install dan di-uninstall diam di CI (`/VERYSILENT`), sehingga diuji otomatis (run #21: install, self-test aplikasi terinstal, uninstall, data pengguna tetap ada).

## Konsekuensi

- Uninstaller tidak menghapus `%LOCALAPPDATA%\CloudSBiian` (cache, staging, basis data) maupun file cloud; dibuktikan di `ci/install-test.txt` ("user data kept after uninstall: True"). WinFsp juga dibiarkan terpasang.
- Pesan uninstall memakai `SuppressibleMsgBox` agar mode diam tidak menggantung (bug yang ditemukan di CI).
- Instalasi WinFsp lewat installer CloudSBiian sendiri belum teruji di CI, karena runner sudah memasang WinFsp sebelumnya sehingga task tersebut tersembunyi.
- Tidak ada code signing; SmartScreen dapat memperingatkan.
