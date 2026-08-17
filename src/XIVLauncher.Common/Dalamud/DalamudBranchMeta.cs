using System;
using System.Collections.Generic;
using System.Net.Http;
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
        try
        {
            branches.AddRange(await FetchFromAsync(client, DistributionConfig.OfficialReleaseBase + "Meta").ConfigureAwait(false));
            officialOk = true;
        }
        catch
        {
            // 公式 Meta 取得失敗は無視(自前分だけでも表示する)。
        }

        // 自前サーバのブランチ一覧をマージ(設定があり、到達できる場合のみ)。
        var customMetaUrl = DistributionConfig.CustomMetaUrl;
        if (!string.IsNullOrEmpty(customMetaUrl))
        {
            try
            {
                branches.AddRange(await FetchFromAsync(client, customMetaUrl).ConfigureAwait(false));
                customOk = true;
            }
            catch
            {
                // 自前 Meta 取得失敗は無視(公式分のみ返す)。
            }
        }

        return (branches, officialOk, customOk);
    }

    public static async Task<IEnumerable<Branch>> FetchBranchesAsync(HttpClient client)
        => (await FetchBranchesDetailedAsync(client).ConfigureAwait(false)).Branches;

    private static async Task<IEnumerable<Branch>> FetchFromAsync(HttpClient client, string url)
    {
        var json = await client.GetStringAsync(url).ConfigureAwait(false);
        var dict = JsonSerializer.Deserialize<Dictionary<string, Branch>>(json);
        return dict == null ? throw new Exception("Failed to deserialize branch metadata.") : dict.Values;
    }
}
