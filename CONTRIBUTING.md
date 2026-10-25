# Contributing

## Lingkungan

- Windows 10/11 x64, .NET SDK 10, (opsional) WinFsp ≥ 2.0 dan rclone untuk integration test, Inno Setup 6 untuk installer.
- `dotnet build CloudSBiian.slnx -c Release`

## Tes

- `tests/CloudSBiian.Tests`: unit test dengan `InMemoryProvider` (laten, kegagalan, rate limit, sesi kedaluwarsa dapat disimulasikan). Tidak butuh jaringan.
- `tests/CloudSBiian.IntegrationTests`: WinFsp mount nyata dan server rclone lokal (WebDAV/SFTP/S3). Otomatis di-skip jika WinFsp/rclone tidak ada (`[WinFspFact]`, `[RcloneFact]`).
- UI self-test: `CloudSBiian.exe --ui-selftest hasil.txt` merender semua halaman (dua tema) dan dialog Add Account untuk semua provider, exit code 0 jika tanpa error.
- CI (`.github/workflows/ci.yml`) menjalankan semuanya di `windows-latest`, membuat paket, menguji install/uninstall, lalu menerbitkan pre-release `latest-build`.

## Aturan

- Tidak ada angka/metrik buatan: tampilkan N/A bila belum terukur.
- Jangan klaim dukungan provider tanpa tes; perbarui `ProviderRegistry`, README, dan TEST_REPORT bersama-sama.
- Rahasia tidak boleh masuk log, settings, atau database — gunakan `ISecretStore` dan `SecretRedactor`.
- Semua path melalui `CloudPath`; jangan menggabungkan path cloud secara manual.
- Operasi berisiko (hapus di cloud, buang perubahan lokal) wajib konfirmasi di UI.
- Gaya kode: nullable aktif, file-scoped namespace, satu tanggung jawab per kelas.
