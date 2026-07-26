# AviUtl2 MCP

AviUtl ExEdit2 のタイムラインを Model Context Protocol (MCP) から参照・編集するためのプロジェクトです。

Repository: <https://github.com/gomi1124/Aviutl2MCP>

PSDToolKit2 と GCMZDrops が導入された Windows 環境を対象に、編集操作、PSD キャラクター制御、診断、自動テストを一体化します。

## 現在の状態

V1のMCP server、AviUtl2 Bridge、PSDToolKit2/GCMZDrops連携、自動診断、配布packageを実装し、33件の受け入れ検証を完了しています。

- 29 tools、5 resources、4 prompts
- revision競合、instance曖昧性、at-most-once、batch/Undoを考慮した編集境界
- PSD作成、setup、character/layer状態、音声・字幕・LAB連携
- MCP stdio、IPC、native、実AviUtl2の分層テスト
- correlation ID、component別log、revision、preview hashを含むdebug report
- `.au2pkg.zip` Bridge packageと自己完結`win-x64` MCP server package

- [Phase 0 仕様書](docs/specification.md)
- [Phase 1 要件定義書](docs/requirements.md)
- [V1 実現性マトリクス](docs/feasibility.md)
- [Phase 2 設計書](docs/design.md)
- [MCP API設計](docs/mcp-api.md)
- [V1 machine-readable Schema catalog](schemas/mcp/v1/catalog.json)
- [IPCプロトコル設計](docs/ipc-protocol.md)
- [PSDToolKit2 / GCMZDrops互換契約](docs/psd-contract.md)
- [V1受け入れテスト対応表](docs/acceptance-test-matrix.md)
- [Phase 3クラス図](docs/class-diagram.md)
- [Phase 4実装計画](docs/implementation-plan.md)

## コンポーネント

- MCP サーバー: MCP クライアントへ tools/resources/prompts を公開
- AviUtl2 ブリッジプラグイン: AviUtl2 SDK を介してタイムラインを安全に操作
- ローカル IPC: MCP サーバーとブリッジプラグインを接続
- 診断・テスト: ログ収集、通信テスト、MCP スモークテスト、AviUtl2 実機テスト

## 前提

- Windows 64-bit
- AviUtl ExEdit2
- PSDToolKit2
- GCMZDrops

導入方法とMCP client設定は[導入・診断ガイド](docs/install.md)を参照してください。

## Build・test・debug

```powershell
dotnet restore .\AviUtl2MCP.slnx --locked-mode
dotnet build .\AviUtl2MCP.slnx --no-restore --configuration Release
.\scripts\Test-McpStdio.ps1 -Configuration Release -NoBuild
.\scripts\Test-BridgeIntegration.ps1 -Configuration Release -NoBuild
.\scripts\Build-Package.ps1 -Configuration Release -Version 0.1.0
```

実AviUtl2テストは既存の編集環境を保護するため、明示的なopt-in、専用fixture copy、runner所有PIDの条件が揃った場合だけ実行します。debug reportは`artifacts\debug`または`artifacts\real-e2e`へ生成されます。

本プロジェクトは [MIT License](LICENSE) で公開します。
