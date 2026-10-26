# estell XIVLauncher 配布物ビルド(フルインストーラ + パッチ適用EXE)
#
#   .\scripts\estell-build-release.ps1 -Version 7.0.22
#
# 2026-07-28 の障害を受けて作成。本体だけを publish して **patcher\ を入れ忘れる**と、
# ゲームのパッチ適用時に「パッチインストーラーが正しく起動できませんでした」で
# 起動不能になる。手順漏れを防ぐため一括化した。
#
# 事前に src/XIVLauncher/XIVLauncher.csproj の VersionPrefix と
# src/XIVLauncher/AppUtil.cs の EstellVersion を更新しておくこと。
# EstellPatcher / EstellSetup の Version/Estell 定数はこのスクリプトが自動で揃える。

[CmdletBinding()]
param(
    # 数値版(csproj の VersionPrefix と一致させる)
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # 生成物の出力先
    [string]$PublishDir = 'C:\Tools\estell-publish',
    [string]$VpkDir     = 'C:\Tools\estell-vpk',
    [string]$OverlayZip = 'C:\Tools\patchsrc\overlay.zip',
    [string]$PatcherPrj = 'C:\Tools\EstellPatcher',
    [string]$SetupPrj   = 'C:\Tools\EstellSetup',

    # 指定するとフルインストーラ(vpk)の生成を省略する
    [switch]$SkipSetup,

    # 指定すると更新フィードの公開まで行う(ランチャーの自動更新はこれを見る)
    [switch]$PublishFeed,

    # 更新フィードの置き場
    [string]$FeedRepo = 'rioriopu/estell-launcher-distrib'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }

# EstellPatcher / EstellSetup の Program.cs にある Version / Estell 定数を実体に合わせる。
# 手で直す運用だと更新漏れが起き、インストーラの「更新があります」ダイアログや
# 完了メッセージが実際とは違う版を表示してしまう。
function Set-VersionConstants([string]$file, [string]$ver, [string]$tag) {
    if (-not (Test-Path $file)) { throw "定数の埋め込み対象が無い: $file" }
    $src = Get-Content $file -Raw -Encoding UTF8
    $new = [regex]::Replace($src, '(const string Version\s*=\s*")[^"]*(")', "`${1}$ver`${2}")
    $new = [regex]::Replace($new, '(const string Estell\s*=\s*")[^"]*(")',  "`${1}$tag`${2}")
    if ($new -cne $src) {
        Set-Content $file $new -Encoding UTF8 -NoNewline
        Write-Host "  版を更新: $file → $ver / $tag"
    } else {
        Write-Host "  版は一致: $file ($ver / $tag)"
    }
}

# estell 版のタグ(estell.N)は AppUtil.cs が正。ここから拾って各 Program.cs に配る。
$appUtil = Join-Path $repo 'src\XIVLauncher\AppUtil.cs'
$m = [regex]::Match((Get-Content $appUtil -Raw -Encoding UTF8), 'EstellVersion\s*=\s*"([^"]+)"')
if (-not $m.Success) { throw "AppUtil.cs から EstellVersion を読めない: $appUtil" }
$EstellTag = $m.Groups[1].Value
Write-Host "estell タグ = $EstellTag (AppUtil.cs)"

# --- 1) 本体 ---------------------------------------------------------------
Step 1 "XIVLauncher 本体を publish → $PublishDir"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
Push-Location (Join-Path $repo 'src\XIVLauncher')
try { dotnet publish -c Release --self-contained -r win-x64 -o $PublishDir } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw '本体の publish に失敗' }

# --- 2) パッチインストーラ(絶対に省略しない) ------------------------------
Step 2 "XIVLauncher.PatchInstaller を publish → $PublishDir\patcher"
Push-Location (Join-Path $repo 'src\XIVLauncher.PatchInstaller')
try { dotnet publish -c Release --self-contained -r win-x64 -o (Join-Path $PublishDir 'patcher') } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'patcher の publish に失敗' }

$patcherExe = Join-Path $PublishDir 'patcher\XIVLauncher.PatchInstaller.exe'
if (-not (Test-Path $patcherExe)) { throw "patcher が生成されていない: $patcherExe" }

# --- 3) ハッシュ一覧 -------------------------------------------------------
Step 3 'hashes.json を生成'
& (Join-Path $PSScriptRoot 'CreateHashList.ps1') $PublishDir

# --- 4) 検証(配布前チェック) ---------------------------------------------
Step 4 '発行物を検証'
$required = @(
    'XIVLauncher.exe'
    'patcher\XIVLauncher.PatchInstaller.exe'   # ← 欠けるとパッチ適用が不能になる
    'Resources\aria2c-xl.exe'                  # ← Aria 取得方式で必要
    'hashes.json'
)
foreach ($r in $required) {
    $p = Join-Path $PublishDir $r
    if (-not (Test-Path $p)) { throw "必須ファイルが無い: $r" }
    Write-Host "  OK  $r"
}
$fv = (Get-Item (Join-Path $PublishDir 'XIVLauncher.dll')).VersionInfo.FileVersion
Write-Host "  FileVersion = $fv"
if ($fv -notlike "$Version*") { throw "版が一致しない(期待 $Version / 実際 $fv)。csproj の VersionPrefix を確認すること" }

# --- 5) フルインストーラ(Velopack) ---------------------------------------
if (-not $SkipSetup) {
    Step 5 "vpk pack → $VpkDir"
    if (Test-Path $VpkDir) { Get-ChildItem $VpkDir | Remove-Item -Recurse -Force }
    else { New-Item -ItemType Directory -Force $VpkDir | Out-Null }
    vpk pack -u XIVLauncher -v $Version -p $PublishDir -e XIVLauncher.exe -o $VpkDir `
        --packTitle 'XIVLauncher (estell)' --packAuthors rioriopu --channel win
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack に失敗' }
}

# --- 6) パッチ適用EXE(自己解凍) ------------------------------------------
Step 6 "overlay.zip を作成 → $OverlayZip"
if (Test-Path $OverlayZip) { Remove-Item $OverlayZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($PublishDir, $OverlayZip, [IO.Compression.CompressionLevel]::Optimal, $false)

Step 7 'estell-xivlauncher-patch.exe をビルド'
Set-VersionConstants (Join-Path $PatcherPrj 'Program.cs') $Version $EstellTag
Push-Location $PatcherPrj
try {
    dotnet publish -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $PatcherPrj 'publish')
} finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'パッチ適用EXE のビルドに失敗' }

# --- 8) フルインストーラEXE(バックアップ付き) --------------------------
# vpk が生成した Setup.exe を埋め込むので、必ず手順 5 の後に実行する。
if (-not $SkipSetup) {
    Step 8 'estell-xivlauncher-setup.exe をビルド'
    Set-VersionConstants (Join-Path $SetupPrj 'Program.cs') $Version $EstellTag
    Push-Location $SetupPrj
    try {
        # 圧縮は付けない。単一ファイル圧縮は実行時に %TEMP% へ展開するため、
        # 展開に失敗すると Main に到達せず、利用者側には一瞬ウィンドウが出て消えるだけになる。
        # サイズは増えるが起動の確実性を優先する。
        dotnet publish -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true `
            -o (Join-Path $SetupPrj 'publish')
    } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'フルインストーラEXE のビルドに失敗' }
}

# --- 9) 更新フィードの公開 ---------------------------------------------------
# ランチャーの自動更新はこのフィードを見る。ここを上げ忘れると
# 利用者側は「更新なし」と判定されて永久に更新されない。
if ($PublishFeed) {
    if ($SkipSetup) { throw '-PublishFeed には vpk の出力が必要。-SkipSetup とは併用できない' }

    Step 9 "更新フィードを $FeedRepo へ公開"

    $feedJson = Join-Path $VpkDir 'releases.win.json'
    if (-not (Test-Path $feedJson)) { throw "releases.win.json が無い。vpk pack の --channel が 'win' か確認すること" }

    # 壊れた JSON を上げると利用者のランチャーが更新を見失う。先に検査する。
    try { Get-Content $feedJson -Raw | ConvertFrom-Json | Out-Null }
    catch { throw "releases.win.json が壊れている: $_" }

    $nupkgs = @(Get-ChildItem $VpkDir -Filter *.nupkg)
    if ($nupkgs.Count -eq 0) { throw 'nupkg が無い。vpk pack が失敗している可能性がある' }
    Write-Host "  nupkg $($nupkgs.Count) 件 / releases.win.json OK"

    # タグ 'win' を使い回す(Velopack のチャンネル名と一致)。
    #
    # gh はタグが無いと stderr に "release not found" を出す。PowerShell はネイティブ
    # コマンドの stderr を ErrorRecord に包むため、$ErrorActionPreference='Stop' の下で
    # リダイレクトすると NativeCommandError で止まってしまう。
    # ここは「無ければ作る」だけなので、判定の間だけ Stop を外して終了コードで見る。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    gh release view win --repo $FeedRepo | Out-Null
    $feedExists = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEap

    if (-not $feedExists) {
        gh release create win --repo $FeedRepo --title 'estell launcher feed (win)' `
            --notes 'Velopack のフィード。estell-build-release.ps1 が自動で更新します。'
        if ($LASTEXITCODE -ne 0) { throw 'Release の作成に失敗' }
    }

    # 本体を先に、一覧を最後に上げる。
    # 逆順だと「一覧には載っているのに本体が無い」瞬間ができる。
    foreach ($n in $nupkgs) {
        gh release upload win $n.FullName --clobber --repo $FeedRepo
        if ($LASTEXITCODE -ne 0) { throw "nupkg のアップロードに失敗: $($n.Name)" }
    }
    gh release upload win $feedJson --clobber --repo $FeedRepo
    if ($LASTEXITCODE -ne 0) { throw 'releases.win.json のアップロードに失敗' }

    Write-Host '  公開しました。VPS へは mirror-to-vps ワークフローが同期します。' -ForegroundColor Green

    # フルインストーラも自前ドメインへ置く。
    # ここを飛ばすとドメイン側だけ古い版が残り続ける。
    # 実際 2026-09-09 に、ドメインが 3 週間前のインストーラを配ったままになっていた。
    $setupExe = Join-Path $SetupPrj 'publish\estell-xivlauncher-setup.exe'
    if (Test-Path $setupExe) {
        Step 10 'フルインストーラを自前ドメインへ配置'

        $key = "$env:USERPROFILE\.ssh\estell_actions"
        if (-not (Test-Path $key)) {
            Write-Warning "  SSH鍵が無いのでドメインへの配置を飛ばす: $key"
        }
        else {
            $dest = 'root@133.167.127.79:/home/ubuntu/estelld-repo/static/estell/launcher/'
            scp -i $key -o BatchMode=yes -o StrictHostKeyChecking=no $setupExe $dest
            if ($LASTEXITCODE -ne 0) { throw 'ドメインへの配置に失敗' }

            # 転送事故の検出。150MB 級なので必ず照合する。
            $localHash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLower()
            $remoteHash = (ssh -i $key -o BatchMode=yes -o StrictHostKeyChecking=no root@133.167.127.79 `
                    'sha256sum /home/ubuntu/estelld-repo/static/estell/launcher/estell-xivlauncher-setup.exe' ).Split(' ')[0]
            if ($localHash -ne $remoteHash) { throw "ドメイン側とハッシュが不一致 (local=$localHash remote=$remoteHash)" }

            Write-Host '  ドメインへ配置し、ハッシュ一致を確認しました。' -ForegroundColor Green
        }
    }
}

Write-Host "`n=== 完成 ===" -ForegroundColor Green
if (-not $SkipSetup) {
    Get-ChildItem (Join-Path $VpkDir 'XIVLauncher-win-Setup.exe') | Select-Object Name, Length
    Get-ChildItem (Join-Path $SetupPrj 'publish\estell-xivlauncher-setup.exe') | Select-Object Name, Length
}
Get-ChildItem (Join-Path $PatcherPrj 'publish\estell-xivlauncher-patch.exe') | Select-Object Name, Length
Write-Host @"

次の手順:
  gh release create xivlauncher-$Version --repo rioriopu/PrivateReleaseRepo ``
    --title "XIVLauncher estell $Version" --notes-file <notes.md> ``
    "$SetupPrj\publish\estell-xivlauncher-setup.exe" ``
    "$PatcherPrj\publish\estell-xivlauncher-patch.exe"

  ※ vpk 素の Setup.exe($VpkDir\XIVLauncher-win-Setup.exe)は
     estell-xivlauncher-setup.exe に埋め込まれているので、単体配布は不要。

  ※ ランチャーの自動更新を効かせるには -PublishFeed を付けて実行すること。
     付けないとフィードが古いままになり、利用者側は「更新なし」と判定される。
"@
