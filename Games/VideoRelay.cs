using Playnite.SDK;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RomM.Games
{
    /// <summary>
    /// Hands video from the RomM server to the Windows media player through an address on this
    /// computer (http://127.0.0.1:port/...). The player built into .NET Framework crashes when
    /// given an https address directly, and RomM servers are usually https.
    ///
    /// Bytes are passed straight through as the player asks for them; nothing is written to disk
    /// here. It listens on this computer only, and only answers requests carrying a key made up
    /// at start-up, so nothing else can use it to reach the server.
    /// </summary>
    public sealed class VideoRelay
    {
        private static readonly ILogger s_log = LogManager.GetLogger();
        private static readonly HttpClient s_http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private readonly object _lock = new object();
        private readonly string _key = Guid.NewGuid().ToString("N");
        private TcpListener _listener;
        private int _port;

        /// <summary>The local address that plays the given server address, or null on failure.</summary>
        public string LocalUrl(string remoteUrl)
        {
            try
            {
                lock (_lock)
                {
                    if (_listener == null)
                    {
                        var listener = new TcpListener(IPAddress.Loopback, 0);
                        listener.Start();
                        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        _listener = listener;
                        Task.Run(() => AcceptLoop(listener));
                    }
                }

                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(remoteUrl))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');
                return $"http://127.0.0.1:{_port}/{_key}/{encoded}/video.mp4";
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Video] Could not start the local relay: {ex.Message}");
                return null;
            }
        }

        private async Task AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    s_log.Warn($"[Video] Local relay stopped: {ex.Message}");
                    return;
                }

                var ignored = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.NoDelay = true;
                    string head = await ReadHead(stream).ConfigureAwait(false);
                    if (head == null)
                        return;

                    string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines[0].Split(' ');
                    if (request.Length < 2)
                        return;

                    string method = request[0].ToUpperInvariant();
                    string remote = RemoteUrl(request[1]);
                    if (remote == null || (method != "GET" && method != "HEAD"))
                    {
                        await WriteText(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                        return;
                    }

                    string range = null;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                            range = lines[i].Substring(6).Trim();
                    }

                    using (var message = new HttpRequestMessage(method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, remote))
                    {
                        if (range != null)
                            message.Headers.TryAddWithoutValidation("Range", range);

                        using (var response = await s_http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        {
                            var reply = new StringBuilder();
                            reply.Append($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
                            reply.Append($"Content-Type: {response.Content.Headers.ContentType?.ToString() ?? "video/mp4"}\r\n");
                            if (response.Content.Headers.ContentLength.HasValue)
                                reply.Append($"Content-Length: {response.Content.Headers.ContentLength.Value}\r\n");
                            ContentRangeHeaderValue contentRange = response.Content.Headers.ContentRange;
                            if (contentRange != null)
                                reply.Append($"Content-Range: {contentRange}\r\n");
                            reply.Append("Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
                            await WriteText(stream, reply.ToString()).ConfigureAwait(false);

                            if (method == "GET")
                            {
                                using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                    await body.CopyToAsync(stream, 64 * 1024).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The player hung up: normal whenever it seeks or the video is stopped.
            }
            catch (SocketException)
            {
            }
            catch (Exception ex)
            {
                s_log.Warn($"[Video] Local relay request failed: {ex.Message}");
            }
        }

        // /<key>/<server address, base64>/video.mp4 -> the server address; null for anything else.
        private string RemoteUrl(string target)
        {
            try
            {
                string[] parts = target.Split('?')[0].Trim('/').Split('/');
                if (parts.Length < 2 || parts[0] != _key)
                    return null;

                string encoded = parts[1].Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
                string url = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                return Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                    && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) ? url : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> ReadHead(Stream stream)
        {
            var buffer = new byte[16 * 1024];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, length, buffer.Length - length).ConfigureAwait(false);
                if (read <= 0)
                    return null;

                length += read;
                string text = Encoding.ASCII.GetString(buffer, 0, length);
                int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                    return text.Substring(0, end);
            }

            return null;
        }

        private static Task WriteText(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }
    }
}
