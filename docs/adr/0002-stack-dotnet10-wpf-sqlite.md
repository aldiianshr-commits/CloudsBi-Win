# ADR-0002: Stack .NET 10 + WPF + SQLite + DPAPI + xUnit

- Status: **Accepted** (mendokumentasikan keputusan yang sudah diimplementasikan; dicatat 2026-10-26)

## Keputusan

| Area | Pilihan | Di mana |
|---|---|---|
| Runtime | .NET 10 (`net10.0` untuk pustaka, `net10.0-windows` untuk Desktop dan VirtualDrive), publish self-contained `win-x64` | `src/*/*.csproj`, CI `setup-dotnet 10.0.x` |
| UI | WPF (`UseWPF`) dengan MVVM ringan sendiri (`Mvvm/Mvvm.cs`), tema gelap/terang | `CloudSBiian.Desktop` |
| Basis data | SQLite lewat `Microsoft.Data.Sqlite` 9.0.0, skema + migrasi | `Core/Storage` |
| Rahasia | DPAPI (`System.Security.Cryptography.ProtectedData`) dengan entropi per akun | `Core/Security/SecretStore.cs` |
| Tes | xUnit 2.9 | `tests/*` |

## Alasan

- **WPF dipilih daripada WinUI 3**: WPF bisa di-build dan dijalankan oleh `dotnet build`/`dotnet publish` biasa di runner CI tanpa Windows App SDK runtime atau paket MSIX, sehingga exe single-file dan ZIP portable lebih sederhana. UI self-test (`--ui-selftest`, 11 halaman × 2 tema) berjalan di runner `windows-latest` (35/35 lulus di run #21). Kekurangan: tampilan tidak memakai kontrol Fluent bawaan Windows 11; tema gelap dibuat manual.
- **.NET 10** adalah rilis LTS yang didukung `setup-dotnet` di CI.
- **SQLite** cukup untuk metadata cache, profil akses, antrean upload, dan library game dalam satu proses; tidak perlu server.
- **DPAPI** mengikat rahasia ke akun Windows pengguna tanpa menyimpan kunci di disk; isolasi antar akun cloud diuji (`SecretTests`: blob akun A tidak bisa dibaca sebagai akun B).

## Konsekuensi

- Desktop dan VirtualDrive hanya bisa di-build di Windows; lingkungan Linux hanya bisa mengedit kode. Semua verifikasi build/test dilakukan di CI Windows.
- `Microsoft.Data.Sqlite` 9.0.0 dipakai di .NET 10; pembaruan ke 10.x bisa dipertimbangkan, tetapi tidak wajib.
