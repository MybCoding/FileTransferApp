using Microsoft.Maui.Storage;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransferApp.Services
{
    public static class RemoteConfigService
    {
        // کانفیگ روی گیت‌هاب:
        // https://github.com/sarbaz1396/FileTransferApp/blob/main/config.json
        // نمونه JSON:
        // {
        //   "windowsInstallerUrl": "https://github.com/sarbaz1396/FileTransferApp/releases/download/v1.0.0/FileTransferApp-Windows-v1.0.0.zip",
        //   "version": "1.0.0"
        // }
        private const string DefaultConfigUrl = "https://raw.githubusercontent.com/MybCoding/FileTransferApp/main/config.json";
        private const string PrefKeyConfigUrl = "RemoteConfigUrl";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private const string PrefKeyUrl = "WinInstallerUrlCache";
        private const string PrefKeyVer = "WinInstallerVersionCache";
        private const string PrefKeyAt = "WinInstallerCachedAtUtc";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

        public static string GetConfigUrl() => Preferences.Get(PrefKeyConfigUrl, DefaultConfigUrl);

        public static void SetConfigUrl(string url)
        {
            url ??= string.Empty;
            Preferences.Set(PrefKeyConfigUrl, url.Trim());
        }

        private const string DefaultWindowsInstallerUrl = "https://github.com/MybCoding/FileTransferApp/releases/download/FileTransferApp-v1.1.1/FileTransferApp-Setup-1.1.1.exe";
        private const string DefaultVersion = "1.1.1";

        public static async Task<(string url, string version)> GetWindowsInstallerUrlAsync(CancellationToken ct = default)
        {
            try
            {
                var configUrl = GetConfigUrl();
                if (string.IsNullOrWhiteSpace(configUrl) || configUrl.Contains("your-domain.com", StringComparison.OrdinalIgnoreCase))
            return (DefaultWindowsInstallerUrl, DefaultVersion);

                using var req = new HttpRequestMessage(HttpMethod.Get, configUrl);
                req.Headers.UserAgent.ParseAdd("FileTransferApp/1.0");

                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();

                var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var model = JsonSerializer.Deserialize<AppDownloadConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (!string.IsNullOrWhiteSpace(model?.WindowsInstallerUrl))
                {
                    Preferences.Set(PrefKeyUrl, model.WindowsInstallerUrl);
                    Preferences.Set(PrefKeyVer, model.Version ?? string.Empty);
                    Preferences.Set(PrefKeyAt, DateTime.UtcNow.ToString("o"));
                    return (model.WindowsInstallerUrl, model.Version);
                }
            }
            catch
            {
                // نادیده گرفتن و تلاش برای کش
            }

            var cachedUrl = Preferences.Get(PrefKeyUrl, string.Empty);
            var cachedVer = Preferences.Get(PrefKeyVer, string.Empty);
            var cachedAtStr = Preferences.Get(PrefKeyAt, string.Empty);

            if (!string.IsNullOrWhiteSpace(cachedUrl))
            {
                if (DateTime.TryParse(cachedAtStr, out var cachedAt))
                {
                    if (DateTime.UtcNow - cachedAt <= CacheTtl)
                        return (cachedUrl, cachedVer);
                }
                else
                {
                    return (cachedUrl, cachedVer);
                }
            }

            return (DefaultWindowsInstallerUrl, DefaultVersion);
        }

        private sealed class AppDownloadConfig
        {
            [JsonPropertyName("windowsInstallerUrl")]
            public string WindowsInstallerUrl { get; set; }

            [JsonPropertyName("version")]
            public string Version { get; set; }
        }
    }
}



