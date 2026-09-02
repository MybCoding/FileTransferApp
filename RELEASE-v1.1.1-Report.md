# FileTransferApp v1.1.1 — Release Report

Release: https://github.com/MybCoding/FileTransferApp/releases/tag/FileTransferApp-v1.1.1

## What Changed in v1.1.1
- **Fixed broken Windows installer download link** in the app (`RemoteConfigService.cs`). Previous versions pointed at `raw/main/dist/...` (non-existent in git). Now points to the real Release asset URL.
- Added **`config.json`** at repo root so the app's runtime remote-config lookup (`raw/main/config.json`) returns 200 instead of 404.
- **Version bump**: `ApplicationDisplayVersion` 1.1.0 → 1.1.1, `ApplicationVersion` (versionCode) 2 → 3.
- Installer script (`installer.iss`) updated to the new publish output path.

## Build Outputs (in `dist\`)
| File | Size |
|---|---|
| `FileTransferApp-1.1.1-android-release.apk` | 54,979,307 B |
| `FileTransferApp-1.1.1-android-release.apk.idsig` | 435,722 B |
| `FileTransferApp-Setup-1.1.1.exe` | 33,662,645 B |
| `FileTransferApp-Windows-v1.1.1.zip` | 47,502,852 B |

## Verification
- **Android APK**: package `com.yazdani.filetransferapp`, `versionCode=3`, `versionName=1.1.1`, `targetSdk=34`.
- **Signature**: same key as v1.1.0 → SHA-256 `40768c38630e93cba4149318d8cf605b6fcacac8e49609c3ffeb75efe66b134e`, DN `CN=Mostafa Yazdani, O=FileTransferApp, ...`. Bazaar update will be accepted.
- **Windows exe**: FileVersion `1.1.1.0`, ProductVersion `1.1.1+3c8e8d1...`.
- Download endpoints (`config.json`, release asset URLs) verified where network allowed.

## Git
- `main` updated to `84399d6` (2 commits on top of `2fa6462`):
  - `3c8e8d1` Fix Windows installer download link; bump to v1.1.1 (versionCode 3)
  - `84399d6` Update installer publish path to win-x64 output directory

## Bazaar (کافهبازار)
Upload `dist\FileTransferApp-1.1.1-android-release.apk` **together with** `dist\FileTransferApp-1.1.1-android-release.apk.idsig`. The `.idsig` is required by modern Bazaar uploads.