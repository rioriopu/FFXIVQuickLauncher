using System;
using System.Collections.Generic;

namespace XIVLauncher.Common.Dalamud;

/// <summary>
/// Dalamud 配布元(公式 kamori / 自前 GitHub 等の静的ホスト)の振り分け設定。
///
/// 公式 goatcorp の配布自体が GitHub ベース(goatcorp/dalamud-distrib + dalamud-declarative)。
/// 同様に自前 GitHub リポジトリ(raw.githubusercontent.com / GitHub Pages)へ静的ファイルを置けば、
/// サーバを立てずに配布できる。静的ホストはクエリ文字列を解釈しないため、自前トラックは
/// パスベース URL で参照する(公式 kamori はクエリ方式のまま)。
///
/// 表示ポリシー(ブランチ切替 UI):
///   - 公式トラックは <see cref="OfficialVisibleTracks"/>(release / stg)のみ常時表示・ベータキー不要。
///   - 自前トラック(<see cref="CustomTracks"/>)はベータキー必須(キー一致時のみ表示)。
///
/// 自前ホストに置くファイル(kamori 相当を静的に自前生成):
///   {CustomReleaseBase}meta.json          -> { "&lt;track&gt;": <see cref="DalamudBranchMeta.Branch"/> } の辞書 (camelCase)
///   {CustomReleaseBase}{track}/version     -> <see cref="DalamudVersionInfo"/> 形式 (PascalCase)。DownloadUrl は zip(Release アセット等)を指す
/// </summary>
public static class DistributionConfig
{
    /// <summary>公式 kamori のリリースベース URL(末尾 '/' 込み)。</summary>
    public const string OfficialReleaseBase = "https://kamori.goats.dev/Dalamud/Release/";

    /// <summary>
    /// 自前配信のベース URL(末尾 '/' 込み)。GitHub raw を想定。
    /// 例: https://raw.githubusercontent.com/&lt;user&gt;/&lt;repo&gt;/main/
    /// 空文字にすると自前配信は無効。
    /// </summary>
    public const string CustomReleaseBase = "https://raw.githubusercontent.com/rioriopu/estell-dalamud-distrib/main/";

    /// <summary>
    /// ブランチ切替 UI で常時表示する公式トラック(ベータキー不要)。
    /// これ以外の公式トラック(canary / api 等)は表示しない。
    /// </summary>
    public static readonly HashSet<string> OfficialVisibleTracks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "release",
            "stg",
        };

    /// <summary>
    /// 自前が配信するトラック名の一覧。ここに無いトラックは公式へ振り分けられる。
    /// 自前トラックはベータキー必須。キーは meta.json / version に公式と同じく <c>key</c> として持たせる。
    /// </summary>
    public static readonly HashSet<string> CustomTracks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "estell",
        };

    /// <summary>指定トラックが自前配信対象かどうか。</summary>
    public static bool IsCustomTrack(string? track) =>
        !string.IsNullOrEmpty(track) && !string.IsNullOrEmpty(CustomReleaseBase) && CustomTracks.Contains(track);

    /// <summary>指定トラックが「公式・常時表示」対象かどうか。</summary>
    public static bool IsOfficialVisibleTrack(string? track) =>
        !string.IsNullOrEmpty(track) && OfficialVisibleTracks.Contains(track);

    /// <summary>
    /// 指定トラックの VersionInfo 取得 URL(完全形)。
    /// 公式はクエリ方式、自前は静的ホスト向けにパス方式({track}/version)。
    /// </summary>
    public static string VersionInfoUrlFor(string track) =>
        IsCustomTrack(track)
            ? $"{CustomReleaseBase}{track}/version"
            : $"{OfficialReleaseBase}VersionInfo?track={track}";

    /// <summary>自前 Meta(ブランチ一覧)の取得 URL。自前配信が無効なら null。</summary>
    public static string? CustomMetaUrl =>
        string.IsNullOrEmpty(CustomReleaseBase) ? null : $"{CustomReleaseBase}meta.json";
}
