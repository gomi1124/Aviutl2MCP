# AviUtl2 MCP

AviUtl ExEdit2 のタイムラインを Model Context Protocol (MCP) から参照・編集するためのプロジェクトです。

PSDToolKit2 と GCMZDrops が導入された Windows 環境を対象に、編集操作、PSD キャラクター制御、診断、自動テストを一体化します。

## 現在の状態

Phase 0（仕様策定）のドラフトを作成済みで、ユーザー承認待ちです。実装内容は承認後の要件定義と設計で確定します。

- [Phase 0 仕様書](docs/specification.md)

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

ライセンスと GitHub リポジトリの公開範囲は未確定です。
