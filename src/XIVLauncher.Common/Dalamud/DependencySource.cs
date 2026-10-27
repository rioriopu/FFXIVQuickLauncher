using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace XIVLauncher.Common.Dalamud;

internal static class DependencySource
{
    public static async Task<T> ReadJson<T>(HttpClient client, IEnumerable<string> sources, Func<string, T> parse)
    {
        Exception? last = null;
        foreach (var url in sources)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var result = parse((await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)).TrimStart('\uFEFF'));
                Log.Information("[DDEPEND] Metadata verified from {Url}", url);
                return result;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warning(ex, "[DDEPEND] Metadata source failed: {Url}", url);
            }
        }
        throw new HttpRequestException("ミラーと公式の配布情報を取得できませんでした。", last);
    }

    public static async Task Download(HttpClient client, string url, string path, Action<long?, long, double?> progress, TimeSpan? inactivityTimeout = null)
    {
        using var headersTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var readTimeout = new CancellationTokenSource();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            // 応答開始だけでなく、転送途中で止まった場合も代替先へ進める。
            readTimeout.CancelAfter(inactivityTimeout ?? TimeSpan.FromSeconds(30));
            var count = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            if (count == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
            total += count;
            var length = response.Content.Headers.ContentLength;
            progress(length, total, length is > 0 ? (double)total / length.Value * 100 : null);
        }
        if (response.Content.Headers.ContentLength is long expected && expected != total)
            throw new IOException("取得ファイルのサイズが一致しません。");
        Log.Information("[DDEPEND] Downloaded {Bytes} bytes from {Url}", total, url);
    }
}
