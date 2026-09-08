using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Dalamud;

public static class DalamudBranchMeta
{
    public class Branch
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("track")]
        public string Track { get; set; }

        [JsonPropertyName("hidden")]
        public bool Hidden { get; set; }

        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("assemblyVersion")]
        public string AssemblyVersion { get; set; }

        [JsonPropertyName("runtimeVersion")]
        public string RuntimeVersion { get; set; }

        [JsonPropertyName("runtimeRequired")]
        public bool RuntimeRequired { get; set; }

        [JsonPropertyName("supportedGameVer")]
        public string SupportedGameVer { get; set; }

        [JsonPropertyName("isApplicableForCurrentGameVer")]
        public bool? IsApplicableForCurrentGameVer { get; set; }

        public string DisplayNameWithAvailability => !this.IsApplicableForCurrentGameVer.GetValueOrDefault(false) ? $"{this.DisplayName} (unavailable)" : this.DisplayName;
    }

    /// <summary>
    ///     ブランチ一覧を取得する。取得できたものだけを返し、片方が落ちていても例外にしない。
    /// </summary>
    /// <returns>
    ///     branches: 取得できたブランチ / officialOk: 公式 Meta が取れたか / customOk: 自前 Meta が取れたか。
    ///     呼び出し側は取得できなかった側のトラックを「消えた」と扱ってはいけない。
    /// </returns>
    public static async Task<(List<Branch> Branches, bool OfficialOk, bool CustomOk)> FetchBranchesDetailedAsync(HttpClient client)
    {
        var branches = new List<Branch>();
        var officialOk = false;
        var customOk = false;

        // 公式 kamori のブランチ一覧。
        // [estell] 本家はここを保護しておらず、kamori が落ちると例外がそのまま伝播して
        // ブランチ切替画面自体が開けなくなっていた(2026-08-17 の GitHub 障害時に発生)。
        // 本家が駄目なら自前 VPS のミラーへ迂回する。
        officialOk = await TryFetchInto(client, branches,
                                        DistributionConfig.OfficialReleaseBase + "Meta",
                                        DistributionConfig.MirrorOfficialMetaUrl).ConfigureAwait(false);

        // 自前サーバのブランチ一覧をマージ(設定があり、到達できる場合のみ)。
        var customMetaUrl = DistributionConfig.CustomMetaUrl;
        if (!string.IsNullOrEmpty(customMetaUrl))
        {
            customOk = await TryFetchInto(client, branches,
                                          customMetaUrl,
                                          DistributionConfig.MirrorCustomMetaUrl).ConfigureAwait(false);
        }

        return (branches, officialOk, customOk);
    }

    /// <summary>
    ///     [estell] primary → mirror の順に取得を試み、成功したものを追加する。
    ///     どちらも駄目なら false(呼び出し側が「一覧が不完全」と判断する)。
    /// </summary>
    private static async Task<bool> TryFetchInto(HttpClient client, List<Branch> into, string primaryUrl, string? mirrorUrl)
    {
        try
        {
            into.AddRange(await FetchFromAsync(client, primaryUrl).ConfigureAwait(false));
            return true;
        }
        catch
        {
            // 本命が駄目でもミラーがあれば続行する。
        }

        if (string.IsNullOrEmpty(mirrorUrl))
            return false;

        try
        {
            into.AddRange(await FetchFromAsync(client, mirrorUrl).ConfigureAwait(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IEnumerable<Branch>> FetchBranchesAsync(HttpClient client)
        => (await FetchBranchesDetailedAsync(client).ConfigureAwait(false)).Branches;

    /// <summary>
    ///     [estell] 本家 declarative の config.yaml から、kamori の一覧に載らないトラックを拾う。
    ///
    ///     kamori の Meta は release / stg しか返さない。api16 のように declarative には
    ///     定義済みでも配信前のトラックは一覧に出てこないため、デバッグ用に定義元を直接見る。
    ///
    ///     YAML パーサは入れていない。この config.yaml は
    ///       tracks:
    ///         &lt;name&gt;:
    ///           key: "..."
    ///           displayName: "..."
    ///     という単純な入れ子しか使わないので、行単位で読めば足りる。
    ///     形式が変わったら黙って空を返す(ここで例外を出して切替画面を壊さない)。
    /// </summary>
    public static async Task<List<Branch>> FetchDeclarativeTracksAsync(HttpClient client)
    {
        const string url = "https://raw.githubusercontent.com/goatcorp/dalamud-declarative/main/config.yaml";
        var result = new List<Branch>();

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };

            using var response = await client.SendAsync(message).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Branch current = null;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                if (line.Length == 0 || line.TrimStart().StartsWith("#"))
                    continue;

                var indent = line.Length - line.TrimStart().Length;
                var trimmed = line.Trim();

                // 「  <name>:」= トラックの開始。値を持たない行だけが該当する。
                if (indent == 2 && trimmed.EndsWith(":") && !trimmed.StartsWith("-"))
                {
                    current = new Branch { Track = trimmed.TrimEnd(':').Trim('"', '\'') };
                    result.Add(current);
                    continue;
                }

                if (current == null || indent < 4)
                    continue;

                var sep = trimmed.IndexOf(':');
                if (sep <= 0)
                    continue;

                var name = trimmed.Substring(0, sep).Trim();
                var value = trimmed.Substring(sep + 1).Trim();

                // 行末コメント(key: "Torgal!"  # awoo?)を落とす。
                // 値が引用符で始まる場合は閉じ引用符までを値とする。
                if (value.StartsWith("\"") || value.StartsWith("'"))
                {
                    var quote = value[0];
                    var end = value.IndexOf(quote, 1);
                    value = end > 0 ? value.Substring(1, end - 1) : value.Trim(quote);
                }
                else
                {
                    var hash = value.IndexOf('#');
                    if (hash >= 0) value = value.Substring(0, hash).Trim();
                }

                switch (name)
                {
                    case "key": current.Key = value; break;
                    case "displayName": current.DisplayName = value; break;
                    case "description": current.Description = value; break;
                    case "applicableGameVersion": current.SupportedGameVer = value; break;
                    case "runtimeVersion": current.RuntimeVersion = value; break;
                    case "hidden": current.Hidden = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                }
            }

            // トラック名を持たない見出し行(tracks: 等)を除く。
            result.RemoveAll(x => string.IsNullOrEmpty(x.Track) || string.IsNullOrEmpty(x.DisplayName));
        }
        catch
        {
            // 取得も解析もできなくてよい。デバッグ用の追加情報にすぎない。
            return [];
        }

        return result;
    }

    private static async Task<IEnumerable<Branch>> FetchFromAsync(HttpClient client, string url)
    {
        // [estell] ブランチ一覧も中間キャッシュを使わせない。
        // ここが古いままだと、切り替え画面に古いバージョンが表示され続ける。
        using var message = new HttpRequestMessage(HttpMethod.Get, url);

        message.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MustRevalidate = true,
        };
        message.Headers.Pragma.ParseAdd("no-cache");

        using var response = await client.SendAsync(message).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var dict = JsonSerializer.Deserialize<Dictionary<string, Branch>>(json);
        return dict == null ? throw new Exception("Failed to deserialize branch metadata.") : dict.Values;
    }
}
