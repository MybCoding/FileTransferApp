# FileTransferApp v1.1.0 — گزارش انتشار

**تاریخ:** ۱۴۰۴۰۲۱۶ (۱ سپتامبر ۲۰۲۶)

## خلاصه

نسخه **۱.۱.۰** اپلیکیشن FileTransferApp برای **اندروید و ویندوز** بهصورت کامل ساخته و در GitHub منتشر شد. سورس به `main` پوش شد و سه فایل باینری از طریق **GitHub Release** منتشر شدند (باینریها در git نگهداری نمیشوند).

## لینکها

| مورد | لینک |
|------|------|
| Repository | https://github.com/MybCoding/FileTransferApp |
| Release v1.1.0 | https://github.com/MybCoding/FileTransferApp/releases/tag/FileTransferApp-v1.1.0 |
| برنچ main | commit `2fa6462` |

> **نکته:** به دلیل مشکل «ghost tag» در GitHub، تگ انتشار `FileTransferApp-v1.1.0` است (نه `v1.1.0`).

## باینریهای منتشرشده (GitHub Release)

| فایل | حجم | توضیح |
|------|-----|--------|
| `FileTransferApp-1.1.0-android-release.apk` | 52.4 MB | APK اندروید (ساینشده) |
| `FileTransferApp-Setup-1.1.0.exe` | 27.4 MB | اینستالر ویندوز (Inno Setup) |
| `FileTransferApp-Windows-v1.1.0.zip` | 39 MB | پکیج ویندوز self-contained (شامل exe + Windows App Runtime) |

همگی وضعیت `uploaded` دارند و Release منتشرشده است (draft نیست).

## سورس در git

- برنچ `main` به commit `2fa6462` بهروز شد (push force).
- مجموع سورس فقط **۲.۸۴ MB** و **۱۲۳ فایل**.
- باینریهای `dist/`، `bin/`، `obj/`، `keystore/`، و `TestResults/` در `.gitignore` هستند (در git نیستند).
- crash dump هفتگی ۱۴۵MB تست از history حذف و تاریخچه تمیز (تک-commit) ساخته شد.

## تغییرات این نسخه (v1.1.0)

- افزایش نسخه به **۱.۱.۰** (`ApplicationDisplayVersion=1.1.0`, `ApplicationVersion=2`) در `FileTransferApp.csproj`
- بهروزرسانی `Services/RemoteConfigService.cs` (آدرس اینستالر و نسخه پیشفرض ۱.۱.۰)
- بهروزرسانی `installer.iss` (`AppVersion=1.1.0`, خروجی `FileTransferApp-Setup-1.1.0.exe`)
- **فیکس:** Open/Save فایلهای دریافتی اندروید (از طریق FileProvider و MediaStore)
- دکمه Connect فقط بعد از pairing نشان داده میشود

## نسخههای قبلی

- **v1.0.0** — tag `v1.0.0` همچنان موجود است (باینریهای قدیمی همانجا).

## خروجیهای محلی (پوشه `dist`)

فایلهای ۱.۱.۰ و ۱.۰.۰ هردو در `dist/` موجودند:
- `FileTransferApp-1.1.0-android-release.apk` (52.4 MB)
- `FileTransferApp-Setup-1.1.0.exe` (27.4 MB)
- `FileTransferApp-Windows-v1.1.0.zip` (39 MB)
- باینریهای `1.0.0` نیز همچنان هست (در صورت نیاز حذف شوند).
