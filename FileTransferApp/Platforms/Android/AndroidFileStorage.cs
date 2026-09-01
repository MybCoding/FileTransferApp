using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Util;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using FileTransferApp.Services;
using AndroidUri = Android.Net.Uri;

namespace FileTransferApp
{
    internal static class AndroidFileStorage
    {
        private const string PrefKeyTreeUri = "AndroidSaveFolderTreeUri";
        private const string PrefKeyPickerPrompted = "AndroidSaveFolderPickerPrompted";
        private const int MaxFileNameLength = 180;

        public const int RequestCodeSaveFolder = 42001;

        private static TaskCompletionSource<AndroidUri> _pickerTcs;

        private static string SavedTreeUri
        {
            get => Preferences.Get(PrefKeyTreeUri, null);
            set => Preferences.Set(PrefKeyTreeUri, value);
        }

        private static bool PickerPrompted
        {
            get => Preferences.Get(PrefKeyPickerPrompted, false);
            set => Preferences.Set(PrefKeyPickerPrompted, value);
        }

        public static async Task<string> SaveReceivedFileAsync(string tempFilePath, string fileName, string senderFolder, string category)
        {
            if (string.IsNullOrWhiteSpace(tempFilePath) || !File.Exists(tempFilePath))
                return null;

            fileName = LimitFileNameLength(fileName);

            try
            {
                string publicPath = null;
                string fallbackPath = null;

                if (category == "Images" || category == "Videos" || category == "Audio")
                {
                    try
                    {
                        publicPath = await SaveMediaAsync(tempFilePath, fileName, senderFolder, category);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("FileTransferApp", $"MediaStore save failed: {ex}");
                    }
                }
                else
                {
                    try
                    {
                        publicPath = await SaveGenericAsync(tempFilePath, fileName, senderFolder, category);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("FileTransferApp", $"SAF save failed: {ex}");
                    }
                }

                // همیشه یک کپی در app-private directory نگه می‌داریم تا مسیر فایل‌سیستم
                // واقعی (قابل File.Exists/باز کردن) در MessageModel.FilePath قرار گیرد.
                // MediaStore/SAF فقط برای رونوشت عمومی در دسترس کاربر است.
                fallbackPath = await SaveToFallbackAsync(tempFilePath, fileName, senderFolder, category);

                // رونوشت عمومی (اگر موفق شد) را نگه می‌داریم، اما مسیر ارجاع اپ = fallback واقعی.
                string result = fallbackPath;
                TryDeleteTemp(tempFilePath);
                return result;
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"AndroidFileStorage.SaveReceivedFileAsync error: {ex}");
                TryDeleteTemp(tempFilePath);
                return null;
            }
        }

        /// <summary>
        /// فایل را فقط به مقصد عمومی (MediaStore یا SAF) ذخیره می‌کند — «Save» دستی کاربر.
        /// رونوشت app-private نمی‌سازد و فایل مبدأ را حذف نمی‌کند. مسیر نمایشی برگردانده می‌شود.
        /// </summary>
        public static async Task<string> SaveToPublicAsync(string sourceFilePath, string fileName, string senderFolder, string category)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
                return null;

            fileName = LimitFileNameLength(fileName);

            try
            {
                if (category == "Images" || category == "Videos" || category == "Audio")
                    return await SaveMediaAsync(sourceFilePath, fileName, senderFolder, category);

                string publicPath = await SaveGenericAsync(sourceFilePath, fileName, senderFolder, category);
                return publicPath;
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"AndroidFileStorage.SaveToPublicAsync error: {ex}");
                return null;
            }
        }

        private static async Task<string> SaveMediaAsync(string tempFilePath, string fileName, string senderFolder, string category)
        {
            var resolver = Platform.AppContext?.ContentResolver;
            if (resolver == null)
            {
                Log.Error("FileTransferApp", "ContentResolver is null; MediaStore save aborted");
                return null;
            }

            string rootDir = category switch
            {
                "Images" => "Pictures",
                "Videos" => "Movies",
                _ => "Music"
            };
            AndroidUri collectionUri = category switch
            {
                "Images" => MediaStore.Images.Media.ExternalContentUri,
                "Videos" => MediaStore.Video.Media.ExternalContentUri,
                _ => MediaStore.Audio.Media.ExternalContentUri
            };

            bool isQPlus = Build.VERSION.SdkInt >= BuildVersionCodes.Q;
            string relPath = isQPlus ? $"{rootDir}/FileTransferApp/{senderFolder}/{category}" : null;
            string uniqueName = GetUniqueMediaName(resolver, collectionUri, relPath, fileName);

            var values = new ContentValues();
            values.Put(MediaStore.MediaColumns.DisplayName, uniqueName);
            values.Put(MediaStore.MediaColumns.MimeType, GetMimeType(uniqueName));
            if (isQPlus)
            {
                values.Put(MediaStore.MediaColumns.RelativePath, relPath);
                values.Put(MediaStore.MediaColumns.IsPending, 1);
            }

            var uri = resolver.Insert(collectionUri, values);
            if (uri == null)
            {
                Log.Error("FileTransferApp", "MediaStore insert returned null");
                return null;
            }

            try
            {
                await using var input = File.Open(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = resolver.OpenOutputStream(uri);
                await input.CopyToAsync(output);
            }
            catch (Exception ex)
            {
                try { resolver.Delete(uri, null, null); } catch { }
                throw;
            }
            finally
            {
                if (isQPlus)
                {
                    try
                    {
                        var update = new ContentValues();
                        update.Put(MediaStore.MediaColumns.IsPending, 0);
                        resolver.Update(uri, update, null, null);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("FileTransferApp", $"MediaStore clear pending error: {ex}");
                    }
                }
            }

            return isQPlus ? $"{relPath}/{uniqueName}" : $"{rootDir}/{uniqueName}";
        }

        private static string GetUniqueMediaName(ContentResolver resolver, AndroidUri collectionUri, string relPath, string fileName)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string selection = null;
            string[] args = null;
            if (!string.IsNullOrEmpty(relPath))
            {
                selection = $"{MediaStore.MediaColumns.RelativePath} = ?";
                args = new[] { relPath };
            }

            using var cursor = resolver.Query(collectionUri, new[] { MediaStore.MediaColumns.DisplayName }, selection, args, null);
            if (cursor != null)
            {
                int nameIdx = cursor.GetColumnIndex(MediaStore.MediaColumns.DisplayName);
                while (cursor.MoveToNext())
                {
                    var name = cursor.GetString(nameIdx);
                    if (!string.IsNullOrEmpty(name)) existing.Add(name);
                }
            }

            return ResolveUniqueName(existing, fileName);
        }

        private static async Task<string> SaveGenericAsync(string tempFilePath, string fileName, string senderFolder, string category)
        {
            string uriString = SavedTreeUri;
            if (!string.IsNullOrWhiteSpace(uriString))
            {
                var treeUri = AndroidUri.Parse(uriString);
                var resolver = Platform.AppContext?.ContentResolver;
                if (treeUri != null && resolver != null && HasPersistedPermission(resolver, treeUri))
                {
                    return await SaveViaDocumentTreeAsync(treeUri, tempFilePath, fileName, senderFolder, category);
                }

                Log.Warn("FileTransferApp", "Stored SAF folder is no longer valid; will request again.");
                SavedTreeUri = null;
                PickerPrompted = false;
            }

            if (!PickerPrompted)
            {
                PickerPrompted = true;
                var pickedUri = await PromptForFolderAsync();
                if (pickedUri != null)
                    return await SaveViaDocumentTreeAsync(pickedUri, tempFilePath, fileName, senderFolder, category);
            }

            return null;
        }

        private static async Task<string> SaveViaDocumentTreeAsync(AndroidUri treeUri, string tempFilePath, string fileName, string senderFolder, string category)
        {
            var resolver = Platform.AppContext?.ContentResolver;
            if (resolver == null)
            {
                Log.Error("FileTransferApp", "ContentResolver is null; SAF save aborted");
                return null;
            }

            string treeDocId = DocumentsContract.GetTreeDocumentId(treeUri);
            string parentDocId = treeDocId;

            string rootName = GetDocumentDisplayName(resolver, treeUri, treeDocId);
            if (!string.Equals(rootName, "FileTransferApp", StringComparison.OrdinalIgnoreCase))
            {
                parentDocId = EnsureDocumentFolder(resolver, treeUri, parentDocId, "FileTransferApp");
                if (parentDocId == null) return null;
            }

            foreach (var segment in new[] { senderFolder, category })
            {
                parentDocId = EnsureDocumentFolder(resolver, treeUri, parentDocId, segment);
                if (parentDocId == null) return null;
            }

            var parentDocUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, parentDocId);
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
            string uniqueName = GetUniqueDocumentName(resolver, childrenUri, fileName);

            var newDocUri = DocumentsContract.CreateDocument(resolver, parentDocUri, GetMimeType(uniqueName), uniqueName);
            if (newDocUri == null)
            {
                Log.Error("FileTransferApp", "SAF CreateDocument returned null");
                return null;
            }

            try
            {
                await using var input = File.Open(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = resolver.OpenOutputStream(newDocUri);
                await input.CopyToAsync(output);
            }
            catch (Exception ex)
            {
                try { DocumentsContract.DeleteDocument(resolver, newDocUri); } catch { }
                throw;
            }

            return $"{senderFolder}/{category}/{uniqueName}";
        }

        private static string GetDocumentDisplayName(ContentResolver resolver, AndroidUri treeUri, string docId)
        {
            var docUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId);
            using var cursor = resolver.Query(docUri, new[] { OpenableColumns.DisplayName }, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                int idx = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (idx >= 0)
                {
                    var name = cursor.GetString(idx);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            return null;
        }

        private static string EnsureDocumentFolder(ContentResolver resolver, AndroidUri treeUri, string parentDocId, string name)
        {
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocId);
            string mimeDir = DocumentsContract.Document.MimeTypeDir;
            string selection = $"{DocumentsContract.Document.ColumnMimeType} = ? AND {DocumentsContract.Document.ColumnDisplayName} = ?";
            using var cursor = resolver.Query(childrenUri, new[] { DocumentsContract.Document.ColumnDocumentId }, selection, new[] { mimeDir, name }, null);
            if (cursor != null && cursor.MoveToFirst())
                return cursor.GetString(0);

            var parentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, parentDocId);
            var newDirUri = DocumentsContract.CreateDocument(resolver, parentUri, mimeDir, name);
            if (newDirUri == null)
            {
                Log.Error("FileTransferApp", $"SAF CreateDocument (folder) returned null for '{name}'");
                return null;
            }
            return DocumentsContract.GetDocumentId(newDirUri);
        }

        private static string GetUniqueDocumentName(ContentResolver resolver, AndroidUri childrenUri, string fileName)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cursor = resolver.Query(childrenUri, new[] { DocumentsContract.Document.ColumnDisplayName }, null, null, null);
            if (cursor != null)
            {
                int nameIdx = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                while (cursor.MoveToNext())
                {
                    var name = cursor.GetString(nameIdx);
                    if (!string.IsNullOrEmpty(name)) existing.Add(name);
                }
            }

            return ResolveUniqueName(existing, fileName);
        }

        private static string ResolveUniqueName(HashSet<string> existing, string fileName)
        {
            if (!existing.Contains(fileName)) return fileName;

            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            while (existing.Contains($"{nameOnly}({counter}){ext}")) counter++;
            return $"{nameOnly}({counter}){ext}";
        }

        private static async Task<string> SaveToFallbackAsync(string tempFilePath, string fileName, string senderFolder, string category)
        {
            var context = Platform.AppContext;
            var externalDir = context?.GetExternalFilesDir(null) ?? context?.FilesDir;
            if (externalDir == null)
            {
                Log.Error("FileTransferApp", "No app-external or internal files directory available for fallback save");
                return null;
            }

            string baseDir = Path.Combine(externalDir.AbsolutePath, "FileTransferApp", "Downloads", senderFolder, category);
            Directory.CreateDirectory(baseDir);

            string destPath = Path.Combine(baseDir, fileName);
            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int counter = 1;
            while (File.Exists(destPath))
            {
                destPath = Path.Combine(baseDir, $"{nameOnly}({counter}){ext}");
                counter++;
            }

            await using var input = File.Open(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = File.Create(destPath);
            await input.CopyToAsync(output);
            return destPath;
        }

        private static string LimitFileNameLength(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length <= MaxFileNameLength) return fileName;

            string ext = Path.GetExtension(fileName);
            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            int maxBase = MaxFileNameLength - ext.Length;
            if (maxBase < 1) maxBase = 1;
            if (nameOnly.Length > maxBase)
                nameOnly = nameOnly.Substring(0, maxBase);
            return nameOnly + ext;
        }

        private static string GetMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                ".heic" or ".heif" => "image/heic",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo",
                ".mkv" => "video/x-matroska",
                ".wmv" => "video/x-ms-wmv",
                ".flv" => "video/x-flv",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".aac" => "audio/aac",
                ".ogg" => "audio/ogg",
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".txt" => "text/plain",
                ".rtf" => "application/rtf",
                ".zip" => "application/zip",
                ".rar" => "application/vnd.rar",
                ".7z" => "application/x-7z-compressed",
                _ => "application/octet-stream"
            };
        }

        private static bool HasPersistedPermission(ContentResolver resolver, AndroidUri treeUri)
        {
            var permissions = resolver.PersistedUriPermissions;
            if (permissions == null) return false;
            foreach (var permission in permissions)
            {
                if (permission.Uri == treeUri && permission.IsWritePermission)
                    return true;
            }
            return false;
        }

        public static void ValidateSavedTreeUri()
        {
            try
            {
                string uriString = SavedTreeUri;
                if (string.IsNullOrWhiteSpace(uriString)) return;

                var treeUri = AndroidUri.Parse(uriString);
                var resolver = Platform.AppContext?.ContentResolver;
                if (treeUri == null || resolver == null || !HasPersistedPermission(resolver, treeUri))
                {
                    Log.Warn("FileTransferApp", "Stored SAF folder permission is no longer valid; it will be requested again when needed.");
                    SavedTreeUri = null;
                    PickerPrompted = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"ValidateSavedTreeUri error: {ex}");
            }
        }

        public static long GetCacheFreeSpace()
            => GetFreeBytes(Platform.AppContext?.CacheDir);

        public static long GetSharedStorageFreeSpace()
            => GetFreeBytes(Android.OS.Environment.ExternalStorageDirectory);

        private static long GetFreeBytes(Java.IO.File directory)
        {
            if (directory == null) return -1;
            try
            {
                var stat = new Android.OS.StatFs(directory.AbsolutePath);
                long blockSize = stat.BlockSizeLong;
                long freeBlocks = stat.AvailableBlocksLong;
                if (blockSize <= 0 || freeBlocks <= 0) return -1;
                try { return checked(freeBlocks * blockSize); }
                catch (OverflowException) { return long.MaxValue; }
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"GetFreeBytes error: {ex}");
                return -1;
            }
        }

        public static void OnFolderPickerResult(Result resultCode, Intent data)
        {
            try
            {
                if (resultCode != Result.Ok || data?.Data == null)
                {
                    Log.Info("FileTransferApp", "Folder picker cancelled or failed");
                    _pickerTcs?.TrySetResult(null);
                    return;
                }

                var uri = data.Data;
                var resolver = Platform.AppContext?.ContentResolver;
                if (resolver == null)
                {
                    Log.Error("FileTransferApp", "ContentResolver is null; cannot persist folder permission");
                    _pickerTcs?.TrySetResult(null);
                    return;
                }

                resolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                SavedTreeUri = uri.ToString();
                Log.Info("FileTransferApp", $"SAF folder persisted: {uri}");
                _pickerTcs?.TrySetResult(uri);
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"OnFolderPickerResult error: {ex}");
                _pickerTcs?.TrySetResult(null);
            }
        }

        private static async Task<AndroidUri> PromptForFolderAsync()
        {
            if (_pickerTcs != null)
                return await _pickerTcs.Task;

            var tcs = new TaskCompletionSource<AndroidUri>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pickerTcs = tcs;

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var page = Microsoft.Maui.Controls.Application.Current?.MainPage;
                    if (page != null)
                    {
                        bool proceed = await page.DisplayAlert(
                            LocalizationResourceManager.T("FolderPickerTitle"),
                            LocalizationResourceManager.T("FolderPickerBody"),
                            LocalizationResourceManager.T("ChooseFolder"),
                            LocalizationResourceManager.T("Later"));
                        if (!proceed)
                        {
                            tcs.TrySetResult(null);
                            return;
                        }
                    }

                    var activity = Platform.CurrentActivity;
                    if (activity == null)
                    {
                        Log.Error("FileTransferApp", "CurrentActivity is null; cannot launch folder picker");
                        tcs.TrySetResult(null);
                        return;
                    }

                    var intent = new Intent(Intent.ActionOpenDocumentTree);
                    intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
                    intent.PutExtra("android.content.extra.SHOW_ADVANCED", true);
                    activity.StartActivityForResult(intent, RequestCodeSaveFolder);
                    Log.Info("FileTransferApp", "SAF folder picker launched");
                }
                catch (Exception ex)
                {
                    Log.Error("FileTransferApp", $"PromptForFolderAsync error: {ex}");
                    tcs.TrySetResult(null);
                }
            });

            return await tcs.Task;
        }

        private static void TryDeleteTemp(string tempFilePath)
        {
            try
            {
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"Temp file delete error: {ex.Message}");
            }
        }

        /// <summary>
        /// فایل را با FileProvider سفارشی (که files-path را پوشش می‌دهد) برای مشاهده
        /// به سیستم می‌سپارد و true برمی‌گرداند اگر قادر به پرتاب Intent بود.
        /// </summary>
        public static async Task<bool> TryOpenWithProviderAsync(string filePath, string mimeType)
        {
            try
            {
                var activity = Platform.CurrentActivity ?? Platform.AppContext as Android.App.Activity;
                if (activity == null) return false;

                var file = new Java.IO.File(filePath);
                if (!file.Exists()) return false;

                var contentUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    Platform.AppContext,
                    "com.yazdani.filetransferapp.fileprovider",
                    file);

                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(contentUri, mimeType);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                intent.AddFlags(ActivityFlags.GrantWriteUriPermission);
                intent.AddFlags(ActivityFlags.GrantPersistableUriPermission);

                var chooser = Intent.CreateChooser(intent, (Java.Lang.ICharSequence)null);
                chooser.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                activity.StartActivity(chooser);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("FileTransferApp", $"TryOpenWithProviderAsync error: {ex}");
                return false;
            }
        }
    }
}
