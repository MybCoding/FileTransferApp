using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Maui.Storage;

namespace FileTransferApp.Services
{
    public static class IconService
    {
        private static readonly HttpClient httpClient;
        private static readonly string cacheDirectory = Path.Combine(FileSystem.CacheDirectory, "Icons");
        private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> inFlight =
            new ConcurrentDictionary<string, Lazy<Task<string>>>();

        private static readonly Dictionary<string, string> IconUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "windows", "https://img.icons8.com/fluency/96/windows-10.png" },
            { "android", "https://img.icons8.com/fluency/96/android.png" },
            { "ios",     "https://img.icons8.com/fluency/96/apple-logo.png" },
            { "macos",   "https://img.icons8.com/fluency/96/mac-os.png" },
            { "linux",   "https://img.icons8.com/color-glass/96/linux.png" },
            { "generic", "https://img.icons8.com/fluency/96/workstation.png" }
        };

        static IconService()
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FileTransferApp-IconService/1.0");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*;q=0.8,*/*;q=0.5");
            EnsureCacheDirectory();
        }

        private static void EnsureCacheDirectory()
        {
            try
            {
                if (!Directory.Exists(cacheDirectory))
                    Directory.CreateDirectory(cacheDirectory);
            }
            catch { }
        }

        private static string NormalizeOS(string os)
        {
            if (string.IsNullOrWhiteSpace(os)) return "generic";
            var s = os.ToLowerInvariant();
            if (s.Contains("android")) return "android";
            if (s.Contains("ios") || s.Contains("apple")) return "ios";
            if (s.Contains("mac") || s.Contains("catalyst")) return "macos";
            if (s.Contains("win") || s.Contains("winui")) return "windows";
            if (s.Contains("linux")) return "linux";
            return "generic";
        }

        public static Task<string> GetIconForOSAsync(string os) => GetIconForOSAsync(os, CancellationToken.None);

        public static async Task<string> GetIconForOSAsync(string os, CancellationToken ct)
        {
            var key = NormalizeOS(os);
            var targetPath = Path.Combine(cacheDirectory, $"{key}.png");

            if (File.Exists(targetPath))
                return targetPath;

            var lazyTask = inFlight.GetOrAdd(key,
                _ => new Lazy<Task<string>>(() => DownloadAndCacheIconAsync(key, targetPath, ct),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return await lazyTask.Value.ConfigureAwait(false);
            }
            catch
            {
                inFlight.TryRemove(key, out _);
                if (!key.Equals("generic", StringComparison.OrdinalIgnoreCase))
                {
                    try { return await GetIconForOSAsync("generic", ct).ConfigureAwait(false); }
                    catch { }
                }
                return "device_icon.png";
            }
        }

        private static async Task<string> DownloadAndCacheIconAsync(string key, string finalPath, CancellationToken ct)
        {
            EnsureCacheDirectory();

            if (!IconUrls.TryGetValue(key, out var url))
                url = IconUrls["generic"];

            var tempPath = finalPath + ".tmp";

            try
            {
                using var res = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();

                await using (var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    await net.CopyToAsync(file, 64 * 1024, ct).ConfigureAwait(false);
                }

                var fi = new FileInfo(tempPath);
                if (!fi.Exists || fi.Length < 128)
                    throw new IOException("Invalid icon.");

                if (File.Exists(finalPath)) { try { File.Delete(finalPath); } catch { } }
                File.Move(tempPath, finalPath);
                System.Diagnostics.Debug.WriteLine("Icon by Icons8 (https://icons8.com)");
                return finalPath;
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        public static async Task PreloadIconsAsync(CancellationToken ct = default)
        {
            var keys = new[] { "windows", "android", "ios", "macos", "linux", "generic" };
            using var sem = new SemaphoreSlim(2, 2);
            var tasks = new List<Task>();

            foreach (var k in keys)
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try { await GetIconForOSAsync(k, ct).ConfigureAwait(false); }
                    catch { }
                    finally { sem.Release(); }
                }, ct));
            }

            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        }

        public static void ClearIconCache()
        {
            try
            {
                if (Directory.Exists(cacheDirectory))
                    Directory.Delete(cacheDirectory, true);
            }
            catch { }
            finally { EnsureCacheDirectory(); }
        }
    }
}