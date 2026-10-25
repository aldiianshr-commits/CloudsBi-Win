# Troubleshooting

| Gejala | Penyebab umum | Solusi |
|---|---|---|
| "WinFsp is not installed" / tombol Mount nonaktif | Driver WinFsp belum terpasang atau < 2.0 | Virtual Drive → **Install WinFsp** (admin), atau https://winfsp.dev/rel/. Restart CloudSBiian. |
| "Drive letter X: is already in use" | Huruf dipakai drive lain/network map | Pilih huruf lain; **Refresh** daftar huruf. |
| Provider berstatus **Blocked** | rclone tidak ditemukan, atau backend tidak ada di build rclone (mis. `terabox`) | Settings → rclone → Check/Browse. Lihat [PROVIDER_SETUP.md](PROVIDER_SETUP.md). |
| Akun `AuthenticationExpired` | Token/cookie kedaluwarsa | Cloud Accounts → **Reconnect** (login ulang hanya akun tersebut). Upload `RetryRequired` → Transfer Queue → **Retry**. |
| Akun `Offline` / file "network error" | Jaringan putus, server tidak terjangkau | Data yang sudah di-cache tetap bisa dibaca; folder ditampilkan dari listing tersimpan. Upload dicoba otomatis saat jaringan kembali. |
| Browser tidak membuka login OAuth | Firewall/port 53682 terpakai | Tutup aplikasi lain yang memakai port tersebut, ulangi Add/Reconnect. |
| Rate limit (Google Drive) | Client ID bersama rclone | Tambah akun dengan client ID/secret sendiri (opsi lanjutan). |
| Upload `Conflict` | File diubah di perangkat lain | Transfer Queue → *Keep mine* / *Keep both* / *Use cloud version*. |
| Cache tidak bertambah | Anggaran penuh atau ruang kosong minimum tercapai | Cache Manager: naikkan anggaran / turunkan batas, atau hapus pin. Pembacaan tetap berjalan tanpa cache. |
| Game tidak mau jalan dari drive | Anti-cheat, launcher, DRM, atau path panjang | Optimize & Prepare membaca penanda; gunakan **Install locally…** (Full Local) atau launcher resminya. |
| Explorer lambat di folder besar | Listing backend lambat | Naikkan "Folder listing cache time" di Settings; hindari folder dengan ribuan item di root. |
| Ingin melaporkan bug | — | Logs & Diagnostics → **Dependency check** dan **Export diagnostics…** (tanpa password/token). |

Lokasi data: `%LOCALAPPDATA%\CloudSBiian` (installer) atau `data\` (portable). Uninstall tidak menghapus folder ini dan tidak menyentuh data cloud; hapus manual jika perlu (pastikan Transfer Queue kosong dulu).
