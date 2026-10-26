<#
  Builds the release artifacts:
    artifacts/publish/CloudSBiian/CloudSBiian.exe   (self-contained, win-x64)
    artifacts/CloudSBiian-Portable-x64.zip
    artifacts/CloudSBiian-Setup.exe                 (needs Inno Setup 6 / ISCC.exe)
  Dependencies (rclone.exe verified against the release SHA256SUMS, winfsp.msi) are downloaded to
  artifacts/deps unless already present. rclone is bundled in all three artifacts.
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

Write-Host "== Dependencies"
# rclone: official release from downloads.rclone.org, checked against the release SHA256SUMS.
$rclone = Join-Path $deps "rclone.exe"
$rcloneCopying = Join-Path $deps "rclone-COPYING.txt"
function Get-Text([string]$url) {
    $tmpFile = Join-Path $deps ("dl-" + [guid]::NewGuid().ToString("N") + ".txt")
    Invoke-WebRequest $url -OutFile $tmpFile -UseBasicParsing
    try { return Get-Content $tmpFile -Raw } finally { Remove-Item $tmpFile -Force }
}
if (-not (Test-Path $rclone)) {
    $rcloneVersion = ((Get-Text "https://downloads.rclone.org/version.txt").Trim() -split "\s+")[1]
    if ($rcloneVersion -notmatch '^v\d+\.\d+\.\d+$') { throw "Unexpected rclone version.txt content: $rcloneVersion" }
    $zipName = "rclone-$rcloneVersion-windows-amd64.zip"
    $zip = Join-Path $deps $zipName
    Invoke-WebRequest "https://downloads.rclone.org/$rcloneVersion/$zipName" -OutFile $zip -UseBasicParsing
    $sums = Get-Text "https://downloads.rclone.org/$rcloneVersion/SHA256SUMS"
    $expected = [regex]::Match($sums, "(?m)^([0-9a-f]{64})\s+\*?$([regex]::Escape($zipName))\s*$").Groups[1].Value
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) { throw "rclone checksum mismatch for $zipName (expected '$expected', got '$actual')" }
    Write-Host "rclone $rcloneVersion downloaded, SHA-256 $actual verified against SHA256SUMS"
    $tmp = Join-Path $deps "rclone-tmp"
    Expand-Archive $zip -DestinationPath $tmp -Force
    Copy-Item (Get-ChildItem $tmp -Recurse -Filter rclone.exe | Select-Object -First 1).FullName $rclone
    Remove-Item -Recurse -Force $tmp, $zip
    Invoke-WebRequest "https://raw.githubusercontent.com/rclone/rclone/$rcloneVersion/COPYING" -OutFile $rcloneCopying -UseBasicParsing
}
if (-not (Test-Path $rcloneCopying)) { throw "rclone license text (COPYING) missing" }
$reported = & $rclone version | Select-Object -First 1
Write-Host "Bundled rclone reports: $reported"
if ($reported -notmatch '^rclone v\d+\.\d+') { throw "Bundled rclone.exe does not run: $reported" }

$winfsp = Join-Path $deps "winfsp.msi"
if (-not (Test-Path $winfsp)) {
    gh release download --repo winfsp/winfsp --pattern "winfsp-*.msi" --dir $deps --clobber
    Move-Item (Get-ChildItem $deps -Filter "winfsp-*.msi" | Select-Object -First 1).FullName $winfsp -Force
}

# rclone.exe (+ its MIT license) is added to the app as content at rclone\rclone.exe, so it is part
# of the publish folder (portable zip, installer) and embedded in the single-file exe.
$bundle = @("-p:BundleRcloneFrom=$rclone", "-p:BundleRcloneLicenseFrom=$rcloneCopying")

Write-Host "== Publishing CloudSBiian $Version (self-contained win-x64)"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root "src\CloudSBiian.Desktop\CloudSBiian.Desktop.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishReadyToRun=false @bundle -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
foreach ($f in "CloudSBiian.exe", "rclone\rclone.exe", "rclone\COPYING.txt") {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "$f missing from publish output" }
}

Write-Host "== Single-file CloudSBiian.exe (rclone embedded)"
$single = Join-Path $artifacts "single"
if (Test-Path $single) { Remove-Item -Recurse -Force $single }
dotnet publish (Join-Path $root "src\CloudSBiian.Desktop\CloudSBiian.Desktop.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true @bundle -o $single
if ($LASTEXITCODE -ne 0) { throw "single-file publish failed" }
$leftovers = Get-ChildItem $single -Recurse -File | Where-Object { $_.Name -notin "CloudSBiian.exe", "CloudSBiian.pdb" -and $_.Extension -ne ".pdb" }
if ($leftovers) { Write-Host "Files next to the single-file exe (not embedded): $($leftovers.Name -join ', ')" }
Copy-Item (Join-Path $single "CloudSBiian.exe") (Join-Path $artifacts "CloudSBiian.exe") -Force

Write-Host "== Portable package"
$portableDir = Join-Path $artifacts "portable\CloudSBiian"
if (Test-Path (Join-Path $artifacts "portable")) { Remove-Item -Recurse -Force (Join-Path $artifacts "portable") }
New-Item -ItemType Directory -Force $portableDir, (Join-Path $portableDir "dependencies") | Out-Null
Copy-Item "$publish\*" $portableDir -Recurse
Copy-Item $winfsp (Join-Path $portableDir "dependencies\winfsp.msi")
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "THIRD_PARTY_NOTICES.md"), (Join-Path $root "README.md") $portableDir
"CloudSBiian portable mode: settings, cache and pending uploads are stored in the 'data' folder next to CloudSBiian.exe." | Out-File -Encoding utf8 (Join-Path $portableDir "portable.flag")
@"
CloudSBiian $Version - portable package (Windows 10/11 x64)

1. The virtual drive needs the WinFsp driver. If it is not installed yet, run
   dependencies\winfsp.msi (administrator rights required) or install it from https://winfsp.dev/rel/
2. Start CloudSBiian.exe. All data is kept in the 'data' folder next to it.
3. rclone (rclone\rclone.exe, MIT license, see rclone\COPYING.txt) is included and used for
   Google Drive, OneDrive, Dropbox, SFTP, S3, SMB. If it is removed, use "Download rclone" or
   "Browse for rclone.exe" on the Accounts page.
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
