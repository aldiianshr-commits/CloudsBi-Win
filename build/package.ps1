<#
  Builds the release artifacts:
    artifacts/publish/CloudSBiian/CloudSBiian.exe   (self-contained, win-x64)
    artifacts/CloudSBiian-Portable-x64.zip
    artifacts/CloudSBiian-Setup.exe                 (needs Inno Setup 6 / ISCC.exe)
  Dependencies (rclone.exe, winfsp.msi) are downloaded to artifacts/deps unless already present.
#>
param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\CloudSBiian"
$deps = Join-Path $artifacts "deps"
New-Item -ItemType Directory -Force $artifacts, $deps | Out-Null

Write-Host "== Publishing CloudSBiian $Version (self-contained win-x64)"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root "src\CloudSBiian.Desktop\CloudSBiian.Desktop.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishReadyToRun=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $publish "CloudSBiian.exe"))) { throw "CloudSBiian.exe missing from publish output" }

Write-Host "== Single-file CloudSBiian.exe"
$single = Join-Path $artifacts "single"
if (Test-Path $single) { Remove-Item -Recurse -Force $single }
dotnet publish (Join-Path $root "src\CloudSBiian.Desktop\CloudSBiian.Desktop.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o $single
if ($LASTEXITCODE -ne 0) { throw "single-file publish failed" }
Copy-Item (Join-Path $single "CloudSBiian.exe") (Join-Path $artifacts "CloudSBiian.exe") -Force

Write-Host "== Dependencies"
$rclone = Join-Path $deps "rclone.exe"
if (-not (Test-Path $rclone)) {
    $zip = Join-Path $deps "rclone.zip"
    Invoke-WebRequest "https://downloads.rclone.org/rclone-current-windows-amd64.zip" -OutFile $zip
    $tmp = Join-Path $deps "rclone-tmp"
    Expand-Archive $zip -DestinationPath $tmp -Force
    Copy-Item (Get-ChildItem $tmp -Recurse -Filter rclone.exe | Select-Object -First 1).FullName $rclone
    Remove-Item -Recurse -Force $tmp, $zip
}
$winfsp = Join-Path $deps "winfsp.msi"
if (-not (Test-Path $winfsp)) {
    gh release download --repo winfsp/winfsp --pattern "winfsp-*.msi" --dir $deps --clobber
    Move-Item (Get-ChildItem $deps -Filter "winfsp-*.msi" | Select-Object -First 1).FullName $winfsp -Force
}

Write-Host "== Portable package"
$portableDir = Join-Path $artifacts "portable\CloudSBiian"
if (Test-Path (Join-Path $artifacts "portable")) { Remove-Item -Recurse -Force (Join-Path $artifacts "portable") }
New-Item -ItemType Directory -Force $portableDir, (Join-Path $portableDir "rclone"), (Join-Path $portableDir "dependencies") | Out-Null
Copy-Item "$publish\*" $portableDir -Recurse
Copy-Item $rclone (Join-Path $portableDir "rclone\rclone.exe")
Copy-Item $winfsp (Join-Path $portableDir "dependencies\winfsp.msi")
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "THIRD_PARTY_NOTICES.md"), (Join-Path $root "README.md") $portableDir
"CloudSBiian portable mode: settings, cache and pending uploads are stored in the 'data' folder next to CloudSBiian.exe." | Out-File -Encoding utf8 (Join-Path $portableDir "portable.flag")
@"
CloudSBiian $Version - portable package (Windows 10/11 x64)

1. The virtual drive needs the WinFsp driver. If it is not installed yet, run
   dependencies\winfsp.msi (administrator rights required) or install it from https://winfsp.dev/rel/
2. Start CloudSBiian.exe. All data is kept in the 'data' folder next to it.
3. rclone (rclone\rclone.exe) is used for Google Drive, OneDrive, Dropbox, SFTP, S3, SMB.
   TeraBox needs an rclone build that includes a 'terabox' backend (see PROVIDER_SETUP.md).
"@ | Out-File -Encoding utf8 (Join-Path $portableDir "README-PORTABLE.txt")
$portableZip = Join-Path $artifacts "CloudSBiian-Portable-x64.zip"
if (Test-Path $portableZip) { Remove-Item $portableZip }
Compress-Archive -Path $portableDir -DestinationPath $portableZip -CompressionLevel Optimal

Write-Host "== Installer"
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup (ISCC.exe) not found" }
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DDepsDir=$deps" (Join-Path $root "installer\CloudSBiian.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $artifacts -File | ForEach-Object { "{0,-40} {1,12:N0} bytes" -f $_.Name, $_.Length }
