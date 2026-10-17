using System;
using System.Net.Http;
using System.Threading;

namespace RomM.Games
{
    // Checks that an image url really serves an image before it is written into a description.
    // RomM can list box art it never managed to store, and the description view draws a coloured
    // placeholder box for an image that fails to load.
    internal static class RomMMediaProbe
    {
        // Client for media hosted outside RomM (Screenscraper), so the RomM credentials on
        // HttpClientSingleton are never sent to a third-party host.
        private static readonly HttpClient s_externalHttp = new HttpClient();

        /// <summary>
        /// A check for the given RomM host. It answers false on any failure, so an image that
        /// cannot be confirmed is left out of the description.
        /// </summary>
        public static Func<string, bool> For(string romMHost, CancellationToken cancelToken)
        {
            return url => IsImage(url, romMHost, cancelToken);
        }

        private static bool IsImage(string url, string romMHost, CancellationToken cancelToken)
        {
            try
            {
                if (string.IsNullOrEmpty(url) || cancelToken.IsCancellationRequested)
                    return false;

                bool servedByRomM = !string.IsNullOrEmpty(romMHost)
                    && url.StartsWith(romMHost + "/", StringComparison.OrdinalIgnoreCase);
                var client = servedByRomM ? HttpClientSingleton.Instance : s_externalHttp;

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    // Headers only: enough to know the file is there without downloading it.
                    using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode)
                            return false;

                        string type = response.Content?.Headers?.ContentType?.MediaType;
                        return type != null && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
