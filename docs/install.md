# 導入・診断

## 1. 前提

- Windows x64
- AviUtl ExEdit2 2.1.3以降（2.1.3aで実機確認済み）
- PSDToolKit2 2.0.0alpha10互換profile
- GCMZDrops API v3

## 2. Bridgeの導入

1. `AviUtl2MCP-Bridge-vX.Y.Z.au2pkg.zip`をAviUtl2のプレビュー画面へD&Dする。
2. 表示されたpackage情報を確認して導入する。
3. AviUtl2を再起動する。
4. `aviutl_get_status`でBridge versionと`ready`状態を確認する。

packageは`Plugin\AviUtl2MCP`だけを所有し、更新・uninstall時はこのsubfolderだけを対象にします。

## 3. MCP serverの設定

1. `AviUtl2MCP-Server-win-x64-vX.Y.Z.zip`を一時directoryへ展開する。
2. AviUtl2を終了し、`Install-AviUtl2MCP.ps1`を実行する。
3. `mcp-config.example.json`をMCP clientの形式へ合わせてコピーする。
4. `command`と`args`を、installerが表示した固定launcherのpathへ変更する。
5. MCP clientを再起動し、33 tools・5 resources・4 promptsを列挙する。

```powershell
.\Install-AviUtl2MCP.ps1
```

既定の導入先は`%LOCALAPPDATA%\AviUtl2MCP`です。固定launcherは検証済みのserverを直ちに起動し、最新のstable GitHub Releaseの確認だけを非同期に行います。draft、prerelease、`main`のartifactは自動導入しません。更新assetは`release-manifest.json`、`checksums.sha256`、byte length、SHA-256、server self-testをすべて通過した場合だけ有効化されます。

Bridgeとserverは同じreleaseへ揃えて切り替えます。AviUtl2が起動中の場合はBridgeを置換せず、`state\update-state.json`を`bridge_pending`にして更新を保留します。AviUtl2を終了すると次回の更新確認でBridgeを更新し、その後にserverを有効化します。以前のBridgeは`bridge-backups`に退避されます。

更新確認の既定間隔は6時間です。MCP起動自体はnetworkを待たず、更新失敗時も`state\active-server.json`が指す直前の検証済みserverを使い続けます。Bridgeを外部で管理する場合だけ、明示的に`-SkipBridgeUpdate`を指定できます。

複数のAviUtl2が起動している場合、編集toolへ対象の`instanceId`を明示します。指定がなければ曖昧な対象への編集は拒否されます。
MCP server全体の既定対象を固定する場合は、環境変数`AVIUTL2_MCP_INSTANCE`へ対象の`instanceId`を指定します。旧実装名`AVIUTL2_MCP_INSTANCE_ID`も互換受付しますが、両方を指定する場合は同じ値にしてください。

## 4. 自動診断

- `aviutl_diagnose`: connection、Bridge、PSDToolKit2、GCMZDrops、read/preview smokeを独立診断する。
- `aviutl_get_logs`: server、Bridge、AviUtl2のlogを相関IDで取得する。
- `scripts/Test-McpStdio.ps1`: MCP black-box testとdebug reportを生成する。
- `scripts/Test-BridgeIntegration.ps1`: IPC切断・再接続testとdebug reportを生成する。
- `scripts/Test-RealAviUtl.ps1 -Real`: 専用copyとrunner所有PIDだけで実機testを実行する。

debug reportは`artifacts\debug`または`artifacts\real-e2e`へ出力され、correlation ID、component別log、repository revision、preview hash欄を含みます。

## 5. 検証とuninstall

配布物のSHA-256は`checksums.sha256`と`release-manifest.json`で確認できます。Bridgeは字幕templateの固定SHA-256を実行時にも検証します。

BridgeはAviUtl2のpackage情報からuninstallします。MCP serverはclient設定を削除してから展開directoryを削除します。
