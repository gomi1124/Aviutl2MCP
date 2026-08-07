# AviUtl2MCP project rules

## 承認済み前提

- 2026-07-18: Phase 1要件、Phase 2設計、Phase 3クラス図・実装計画を承認済み。
- 2026-07-18: `gomi1124/Aviutl2MCP`を公開GitHub repositoryとして作成し、feature branchをpushすることを承認済み。
- PSDToolKit2と、その連携に必要なplugin（GCMZDropsを含む）は導入済みとして設計・実装・実機testを行う。
- 2026-08-08: 公開SDKにscene切替APIがないため、`aviutl_open_scene`はscene list UIを操作し、SDKのscene IDで事後検証する実験機能として実装することを承認済み。

## 実機test

- 実AviUtl2を操作するtestは明示的なopt-in環境変数でのみ起動する。
- test runnerが起動したprocessとcorrelation ID配下の一時artifactだけをcleanup対象にする。
- 自動debug reportにはcorrelation ID、component別log、revision、preview hashを含める。
