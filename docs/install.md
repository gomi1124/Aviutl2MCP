# 導入・診断

## 1. 前提

- Windows x64
- AviUtl ExEdit2 2.1.0以降
- PSDToolKit2 2.0.0alpha10互換profile
- GCMZDrops API v3

## 2. Bridgeの導入

1. `AviUtl2MCP-Bridge-vX.Y.Z.au2pkg.zip`をAviUtl2のプレビュー画面へD&Dする。
2. 表示されたpackage情報を確認して導入する。
3. AviUtl2を再起動する。
4. `aviutl_get_status`でBridge versionと`ready`状態を確認する。

packageは`Plugin\AviUtl2MCP`だけを所有し、更新・uninstall時はこのsubfolderだけを対象にします。

## 3. MCP serverの設定

1. `AviUtl2MCP-Server-win-x64-vX.Y.Z.zip`を任意の固定directoryへ展開する。
2. `mcp-config.example.json`をMCP clientの形式へ合わせてコピーする。
3. `command`を`AviUtl2MCP.Server.exe`の絶対pathへ変更する。
4. MCP clientを再起動し、28 tools・5 resources・4 promptsを列挙する。

複数のAviUtl2が起動している場合、編集toolへ対象の`instanceId`を明示します。指定がなければ曖昧な対象への編集は拒否されます。

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
