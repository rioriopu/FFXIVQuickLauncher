# estell XIVLauncher 配布物ビルド(フルインストーラ + パッチ適用EXE)
#
#   .\scripts\estell-build-release.ps1 -Version 7.0.22
#
# 2026-07-28 の障害を受けて作成。本体だけを publish して **patcher\ を入れ忘れる**と、
# ゲームのパッチ適用時に「パッチインストーラーが正しく起動できませんでした」で
# 起動不能になる。手順漏れを防ぐため一括化した。
#
# 事前に src/XIVLauncher/XIVLauncher.csproj の VersionPrefix、
# src/XIVLauncher/AppUtil.cs の EstellVersion、
# C:\Tools\EstellPatcher の Version/Estell 定数を更新しておくこと。

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
    [switch]$SkipSetup
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }

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
    Push-Location $SetupPrj
    try {
        dotnet publish -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
            -o (Join-Path $SetupPrj 'publish')
    } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'フルインストーラEXE のビルドに失敗' }
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
"@
