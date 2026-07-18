# AviUtl2 MCP

AviUtl ExEdit2 のタイムラインを Model Context Protocol (MCP) から参照・編集するためのプロジェクトです。

Repository: <https://github.com/gomi1124/Aviutl2MCP>

PSDToolKit2 と GCMZDrops が導入された Windows 環境を対象に、編集操作、PSD キャラクター制御、診断、自動テストを一体化します。

## 現在の状態

Phase 0～2はユーザー承認済みです。Phase 3（クラス図・実装分解）は敵対的レビューまで完了し、Phase 4（実装・自動テスト）へ移行します。

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

## 想定コンポーネント

- MCP サーバー: MCP クライアントへ tools/resources/prompts を公開
- AviUtl2 ブリッジプラグイン: AviUtl2 SDK を介してタイムラインを安全に操作
- ローカル IPC: MCP サーバーとブリッジプラグインを接続
- 診断・テスト: ログ収集、通信テスト、MCP スモークテスト、AviUtl2 実機テスト

## 前提

- Windows 64-bit
- AviUtl ExEdit2
- PSDToolKit2
- GCMZDrops

本プロジェクトは [MIT License](LICENSE) で公開します。
