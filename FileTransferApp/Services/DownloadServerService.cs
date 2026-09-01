using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileTransferApp.Services
{
    /// <summary>
    /// Lightweight temporary LAN HTTP server (no ASP.NET/Kestrel dependencies).
    /// Endpoints:
    ///  GET /        -> landing HTML page
    ///  GET /download-> serve local file OR redirect to public URL
    ///  GET /health  -> OK
    /// </summary>
    public sealed class DownloadServerService : IAsyncDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private int _port;
        private string _redirectUrl;
        private string _localFilePath;
        private Timer _autoStopTimer;

        public bool IsRunning => _acceptLoop != null && !_acceptLoop.IsCompleted;
        public int Port => _port;
        public string BaseUrl => $"http://{GetLocalIPv4()}:{_port}";

        public async Task StartAsync(int port = 8080, string redirectUrl = null, string localFilePath = null, TimeSpan? autoStop = null)
        {
            if (IsRunning) return;

            _port = port;
            _redirectUrl = redirectUrl;
            _localFilePath = localFilePath;
            // It's OK to start even if download is not configured yet.

            _cts = new CancellationTokenSource();

            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

            if (autoStop.HasValue)
            {
                _autoStopTimer?.Dispose();
                _autoStopTimer = new Timer(async _ =>
                {
                    try { await StopAsync(); } catch { }
                }, null, autoStop.Value, Timeout.InfiniteTimeSpan);
            }

            await Task.Delay(150);
        }

        public async Task StopAsync()
        {
            try
            {
                _autoStopTimer?.Dispose();
                _autoStopTimer = null;

                if (!IsRunning) return;

                _cts?.Cancel();
                try { _listener?.Stop(); } catch { }

                if (_acceptLoop != null)
                {
                    try { await _acceptLoop; } catch { }
                }
            }
            finally
            {
                _acceptLoop = null;
                _cts?.Dispose();
                _cts = null;

                _listener = null;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }

                    _ = Task.Run(() => HandleClientAsync(client, ct));
                }
            }
            catch { /* ignore */ }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using var _ = client;
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 10000;

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                // Consume headers
                string line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    // ignore
                }

                // Parse: METHOD PATH HTTP/1.1
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0].Trim();
                var rawPath = parts[1].Trim();
                var path = rawPath.Split('?', '#')[0];

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(stream, 405, "Method Not Allowed", "Method Not Allowed");
                    return;
                }

                if (path == "/" || path == string.Empty)
                {
                    await WriteLandingAsync(stream);
                    return;
                }

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextAsync(stream, 200, "OK", "OK");
                    return;
                }

                if (path.Equals("/download", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteDownloadAsync(stream);
                    return;
                }

                await WriteTextAsync(stream, 404, "Not Found", "Not Found");
            }
            catch
            {
                // ignore
            }
        }

        private async Task WriteLandingAsync(Stream stream)
        {
            bool hasDownload = (!string.IsNullOrWhiteSpace(_localFilePath) && File.Exists(_localFilePath)) ||
                               !string.IsNullOrWhiteSpace(_redirectUrl);

            string bodyHtml = hasDownload
                ? @"<p><a class=""btn"" href=""/download"">دانلود</a></p>
  <p class=""note"">اگر دانلود به‌طور خودکار شروع نشد، لینک بالا را کلیک کنید.</p>
  <script>setTimeout(function(){ window.location.href='/download'; }, 900);</script>"
                : @"<p class=""warn"">لینک دانلود هنوز روی موبایل تنظیم نشده است. لطفاً بعداً دوباره تلاش کنید.</p>";

            string html = @"<!doctype html>
<html lang=""fa""><head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>دانلود اپ ویندوز</title>
<style>
body { font-family:sans-serif; background:#f7f7f7; color:#333; }
.container { max-width:680px; margin:40px auto; background:#fff; padding:24px 20px; border-radius:12px; box-shadow:0 4px 20px rgba(0,0,0,0.06); }
h1 { font-size:20px; margin-top:0; }
.btn { display:inline-block; background:#2196F3; color:#fff; padding:10px 16px; border-radius:8px; text-decoration:none; }
.note { color:#666; font-size:14px; line-height:1.7; }
.warn { color:#B71C1C; background:#FFEBEE; padding:10px 12px; border-radius:10px; }
</style>
</head><body>
<div class=""container"">
  <h1>دانلود نسخه ویندوز</h1>
  <p class=""note"">اگر این صفحه را روی کامپیوتر خود دیدید، برای دانلود برنامه ویندوز کافی‌ست روی دکمه زیر کلیک کنید.</p>
" + bodyHtml + @"
</div>
</body></html>";

            var body = Encoding.UTF8.GetBytes(html);
            var header = BuildHeader(200, "OK",
                contentType: "text/html; charset=utf-8",
                contentLength: body.Length,
                extraHeaders: "Cache-Control: no-cache, no-store, must-revalidate\r\n");

            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(body, 0, body.Length);
        }

        private async Task WriteDownloadAsync(Stream stream)
        {
            // local file
            if (!string.IsNullOrWhiteSpace(_localFilePath) && File.Exists(_localFilePath))
            {
                var fileName = Path.GetFileName(_localFilePath);
                var fi = new FileInfo(_localFilePath);

                var header = BuildHeader(200, "OK",
                    contentType: "application/octet-stream",
                    contentLength: fi.Length,
                    extraHeaders:
                        "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                        $"Content-Disposition: attachment; filename=\"{fileName}\"\r\n");

                await stream.WriteAsync(header, 0, header.Length);

                await using var fs = File.OpenRead(_localFilePath);
                await fs.CopyToAsync(stream);
                return;
            }

            // redirect
            if (!string.IsNullOrWhiteSpace(_redirectUrl))
            {
                var header = BuildHeader(302, "Found",
                    contentType: "text/plain; charset=utf-8",
                    contentLength: 0,
                    extraHeaders:
                        "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                        $"Location: {_redirectUrl}\r\n");

                await stream.WriteAsync(header, 0, header.Length);
                return;
            }

            await WriteTextAsync(stream, 404, "Not Found", "Download not available.");
        }

        private static async Task WriteTextAsync(Stream stream, int statusCode, string statusText, string text)
        {
            var body = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var header = BuildHeader(statusCode, statusText,
                contentType: "text/plain; charset=utf-8",
                contentLength: body.Length,
                extraHeaders: "Cache-Control: no-cache, no-store, must-revalidate\r\n");

            await stream.WriteAsync(header, 0, header.Length);
            if (body.Length > 0)
                await stream.WriteAsync(body, 0, body.Length);
        }

        private static byte[] BuildHeader(int statusCode, string statusText, string contentType, long contentLength, string extraHeaders)
        {
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Server: FileTransferApp\r\n");
            if (!string.IsNullOrWhiteSpace(contentType))
                sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {contentLength}\r\n");
            if (!string.IsNullOrWhiteSpace(extraHeaders))
                sb.Append(extraHeaders);
            sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static string GetLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var ip = ua.Address.ToString();
                            if (IsPrivateIPv4(ip)) return ip;
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private static bool IsPrivateIPv4(string ip) =>
            !string.IsNullOrWhiteSpace(ip) &&
            (ip.StartsWith("10.") ||
             ip.StartsWith("192.168.") ||
             ip.StartsWith("172.16.") || ip.StartsWith("172.17.") || ip.StartsWith("172.18.") || ip.StartsWith("172.19.") ||
             ip.StartsWith("172.20.") || ip.StartsWith("172.21.") || ip.StartsWith("172.22.") || ip.StartsWith("172.23.") ||
             ip.StartsWith("172.24.") || ip.StartsWith("172.25.") || ip.StartsWith("172.26.") || ip.StartsWith("172.27.") ||
             ip.StartsWith("172.28.") || ip.StartsWith("172.29.") || ip.StartsWith("172.30.") || ip.StartsWith("172.31."));

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}

