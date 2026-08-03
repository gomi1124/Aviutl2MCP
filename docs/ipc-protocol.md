# AviUtl2 MCP IPCプロトコル設計

## 1. 適用範囲

本プロトコルは `.NET` MCPサーバーと、AviUtl2プロセス内のC++ブリッジプラグインを接続する。MCP JSON-RPCをそのまま転送せず、AviUtl2操作用のバージョン付き内部契約とする。

- protocol: `A2MP`
- V1 major/minor: `1.0`
- transport: Windows named pipe、duplex、byte mode、overlapped I/O
- encoding: 明示的なlittle-endian header、UTF-8 JSON、任意binary
- pipe server: AviUtl2ブリッジ
- pipe client: MCPサーバー

## 2. エンドポイント

```text
\\.\pipe\AviUtl2MCP.v1.{instanceId}
```

- `instanceId`はプラグイン起動ごとに生成する128-bit UUID。
- V1は `nMaxInstances=8` とし、同じAviUtl2へ最大8つのMCPサーバーが同時接続できる。
- 全instanceで `PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED` を使い、最初の待受instanceだけ `FILE_FLAG_FIRST_PIPE_INSTANCE` を付ける。
- `PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS`
- 入出力pipe bufferは64 KiBを目安とし、フレームサイズや境界をpipe bufferへ依存させない。

各pipe instanceは独立してhandshakeとIPCを処理する。AviUtl2 SDK操作は全sessionで共有するcommand gateへ投入して直列化し、同時接続中もSDK呼び出しを競合させない。9つ目以降のクライアントは空きinstanceができるまで待機し、クライアント側の接続timeout内に空かなければ接続失敗とする。

## 3. Windows認可

1. AviUtl2プロセストークンの `TokenGroups` から `SE_GROUP_LOGON_ID` のSIDを取得する。
2. そのlogon SIDとSYSTEMだけに必要なpipe読取・書込・同期権限を与える保護DACLを作る。
3. `SECURITY_ATTRIBUTES.bInheritHandle = FALSE` とする。
4. SIDまたはDACL生成に失敗した場合、既定DACLへフォールバックせずIPC起動を失敗させる。
5. 接続後に `GetNamedPipeClientProcessId` と `GetNamedPipeClientSessionId` を取得して診断へ記録する。

同一logon SID内はV1の信頼境界とする。pipe名を認証秘密として扱わない。

## 4. インスタンスディスクリプター

```text
%LOCALAPPDATA%\AviUtl2MCP\v1\instances\{instanceId}.json
```

```json
{
  "instanceId": "019f...",
  "processId": 1234,
  "processCreationTime": 134135000000000000,
  "pipeName": "AviUtl2MCP.v1.019f...",
  "bridgeVersion": "0.2.1",
  "protocolMajor": 1
}
```

- pipe待受開始後、一時ファイルを書いて同一ディレクトリ内のatomic replaceで公開する。
- 正常終了時は自身のファイルだけ削除する。
- 読取側はPID、`GetProcessTimes`のcreation time、handshakeのinstanceIdをすべて照合する。
- stale descriptorやPID再利用を候補にしない。
- ディスクリプターへproject path、字幕、alias、秘密値を書かない。

## 5. 40-byte frame header

C++構造体を直接送信せず、各フィールドを明示的にencode/decodeする。

| Offset | Size | Field | 規則 |
|---:|---:|---|---|
| 0 | 4 | magic | ASCII `A2MP` |
| 4 | 2 | headerSize | `40` |
| 6 | 1 | protocolMajor | `1` |
| 7 | 1 | protocolMinor | `0` |
| 8 | 1 | messageKind | 下表 |
| 9 | 1 | flags | V1は定義済みbit以外0 |
| 10 | 2 | reserved | 0 |
| 12 | 16 | requestId | RFC 4122 network byte orderのUUID |
| 28 | 4 | jsonLength | little-endian unsigned、最大8 MiB |
| 32 | 8 | binaryLength | little-endian unsigned、最大16 MiB |

frameはheader、JSON bytes、binary bytesの順とする。受信側はheader時点で値、加算overflow、上限を検証してから確保する。不正magic、header、長さ、UTF-8、未知flagを検出したら、バイト列から再同期せず接続を閉じる。

## 6. Message kind

| Value | Kind | JSON | Binary |
|---:|---|---|---|
| 1 | `ClientHello` | 必須 | なし |
| 2 | `ServerHello` | 必須 | なし |
| 3 | `Request` | 必須 | 操作別 |
| 4 | `Response` | 必須 | 操作別 |
| 5 | `Cancel` | 任意 | なし |
| 6 | `CancelAck` | 必須 | なし |
| 7 | `Ping` | 任意 | なし |
| 8 | `Pong` | 任意 | なし |
| 9 | `Close` | 任意 | なし |

V1のflags:

- bit 0: binaryあり
- bit 1: error response
- bit 2: partial response

`Request`はclientが生成した非zero `requestId`、`Response`は同じ値を使う。変更RequestのIDはUUIDv7を必須とする。`Cancel`と`CancelAck`は取消対象のrequest IDを使う。`ClientHello`/`ServerHello`と`Ping`/`Pong`は各組で同じ非zero IDを使い、`Close`だけzero UUIDを許可する。

## 7. Handshake

最初のframeは必ず `ClientHello` とし、それ以前のRequestを拒否する。

```json
{
  "clientInstanceId": "uuid",
  "clientProcessId": 4567,
  "targetInstanceId": "uuid",
  "protocol":{"minMajor":1,"minMinor":0,"maxMajor":1,"maxMinor":0},
  "clientVersion":"0.2.1",
  "limits":{"jsonBytes":8388608,"binaryBytes":16777216,"inFlight":8}
}
```

```json
{
  "accepted":true,
  "instanceId":"uuid",
  "serverEpoch":"uuid",
  "aviutlProcessId":1234,
  "aviutlProcessCreationTime":134135000000000000,
  "protocol":{"major":1,"minor":0},
  "versions":{"bridge":"0.2.1","sdk":"2010300","aviutl":"2010300"},
  "limits":{"jsonBytes":8388608,"binaryBytes":16777216,"inFlight":8},
  "capabilities":{}
}
```

- 実client PIDは `GetNamedPipeClientProcessId` を正とし、JSON値が違えば拒否する。
- `serverEpoch`はbridge plugin初期化時に1回生成し、同じplugin lifetime内のpipe再接続では変えない。target instance、process ID、creation time、server epochを接続後も保持する。
- major不一致は次の `ServerHello` を返してCloseする。minorは両者が対応する小さい値を選ぶ。
- 上限は両者が提示した小さい値を採用する。

```json
{
  "accepted":false,
  "error":{"code":"protocol_incompatible","message":"protocol major rangeが一致しません"},
  "clientRange":{"minMajor":1,"minMinor":0,"maxMajor":1,"maxMinor":0},
  "serverRange":{"minMajor":2,"minMinor":0,"maxMajor":2,"maxMinor":0}
}
```

## 8. Request / Response envelope

### 8.1 Request

```json
{
  "method":"object.move",
  "correlationId":"uuid",
  "timeoutMs":10000,
  "expectedRevision":"opaque",
  "dryRun":false,
  "params":{}
}
```

`requestId`はheaderだけに置く。`correlationId`はMCP応答と全ログを関連付ける値で、通常はrequest IDと同じUUIDを使う。`method`は内部operation名であり、MCP tool名と一対一である必要はない。

### 8.2 Success

```json
{
  "ok":true,
  "correlationId":"uuid",
  "instanceId":"uuid",
  "revision":"opaque",
  "viewRevision":"opaque",
  "result":{},
  "warnings":[]
}
```

### 8.3 Error / partial

```json
{
  "ok":false,
  "correlationId":"uuid",
  "instanceId":"uuid",
  "revision":"opaque",
  "viewRevision":"opaque",
  "result":{},
  "error":{
    "code":"partial_operation",
    "message":"一部の変更が適用されました。",
    "retryable":false,
    "phase":"preflight|sdk|external|postcondition",
    "outcome":"not_started|unchanged|unknown|partial|completed",
    "undoRecommended":true,
    "details":{}
  }
}
```

`result`は通常の失敗では省略し、`partial_operation`または結果不明timeoutで安全に再取得できた適用済み状態だけを含める。内部例外の型、address、stack、未マスクpathを応答へ含めない。

## 9. 内部operation

| 分類 | operation prefix | 実行先 |
|---|---|---|
| 状態 | `status.*`, `capabilities.*`, `project.*` | cache + SDK |
| timeline | `timeline.*`, `object.*`, `effect.*` | read/edit section |
| UI | `view.*` | edit section |
| batch | `batch.execute` | preflight + single edit section |
| preview | `preview.render` | async SDK render + WIC |
| PSD | `psd.*` | SDK + GCMZDrops + postcondition |
| diagnostics | `diagnostics.*`, `logs.*` | cache/log/OS/SDK |

operationの正確な入力・出力DTOは同じversionの`schemas/ipc/`へ保存し、C#とC++のgolden contract testで照合する。

`project.save`は変更operationとしてat-most-once管理するが、project内容を変更しないためcontent revisionを増加させない。host command送信後にsave callbackを確認できない場合は`outcome="unknown"`を返し、同じrequest IDを再実行しない。

## 10. Binary payload

V1でbinaryを使う応答は主にpreview PNGとする。

```json
{
  "ok":true,
  "result":{
    "frame":1,
    "width":1920,
    "height":1080,
    "mimeType":"image/png",
    "binaryLength":123456,
    "sha256":"64 lowercase hex"
  }
}
```

- binaryはJSON直後へ連結する。
- JSONのlengthとheaderのbinaryLengthを一致させる。
- 受信後にSHA-256、PNG signature、最大寸法を検証する。
- V1はcompression flagや複数binary attachmentを導入しない。

## 11. 同時実行と書込み

- 1接続あたり既定8、最大16要求を受け付ける。
- pipe read、SDK command、pipe writeを別キューにする。
- SDKを使う操作は全インスタンス共通Command gateで1件ずつ実行する。
- previewは同時1件にする。
- pipeへのwriteは接続ごとにsingle writer queueだけが行い、並列responseのbyte混在を防ぐ。
- responseはrequestIdで対応付け、要求順と異なる順で返ることを許す。
- bridge queue上限64を超えた要求は `request_too_large` ではなく `bridge_busy` として再試行可能エラーにする。

## 12. At-most-onceと再接続

変更要求のキーを `(serverEpoch, clientInstanceId, requestId)` とする。

```text
accepted -> queued -> executing -> completed
                         \-> unknown / partial
```

- 同じID・同じpayload SHA-256が再送された場合、queued/executing中は再接続した要求を同じ実行結果へattachし、完了後はキャッシュ済みの同一応答を返す。
- 同じIDでpayloadが違う場合は `request_id_conflict` とする。
- UUIDv7 timestampが10分より古い変更要求、または5分より未来の要求は実行せず `request_expired` とする。
- payload hashは `protocolMajor || flags || jsonLengthLE || 受信した正確なUTF-8 JSON bytes || binaryLengthLE || binary bytes` のSHA-256とし、headerのrequest IDは含めない。再送側は同じframe body bytesを保持する。
- ID/hash/state/outcome/revision/result digestの小さいtombstoneをUUIDv7 timestampから10分後まで最大4096件保持する。未失効tombstoneを削除せず、満杯時は新規変更要求を `bridge_busy` とする。
- 完全JSON応答は1件64 KiB以下、最大256件のLRUへ別管理する。tombstoneはあるが完全応答が失効した再送には、最終outcome/revision/result digestを伴う `request_result_evicted` を返し、再実行しない。binary previewは対象外とする。
- 接続断後、同じMCPサーバープロセスは同じclientInstanceId/requestIdで未確定要求を照会できる。
- 選択したAviUtl2が消えた場合、別インスタンスへ自動移行しない。
- `serverEpoch`変更後は以前のキャッシュを利用できないため、変更要求を新IDで自動再実行しない。

## 13. Timeoutとcancel

| 状態 | cancel動作 |
|---|---|
| 未受付・queue中 | 取消して変更なし |
| read中 | callback終了後に結果を破棄可能 |
| edit section開始前 | 取消して変更なし |
| edit section開始後 | 強制中断せず実際の結果を確定 |
| GCMZDrops送信後 | 事後検索まで継続 |
| render投入後 | callerをabandonedにできるがcallback contextは完了まで保持 |

- MCP cancellationを受けたBridgeClientはCancelを送り、commit point前だけ取消す。
- `call_edit_section`、`WM_COPYDATA`、SDK renderに安全な強制取消を仮定しない。
- timeout後も内部要求を追跡し、最終結果をcorrelation logへ記録する。
- 変更要求を新しいrequestIdで自動再試行しない。

`CancelAck` body:

```json
{"status":"cancelled|tooLate|notFound","responseWillFollow":true}
```

- `cancelled`: 元Requestへ `operation_cancelled` Responseを1件返し、その後のResponseはない。
- `tooLate`: commit point後なので元Requestの最終Responseが後から1件返る。
- `notFound`: 対象IDを受理しておらず、元RequestのResponseを新たに生成しない。
- `responseWillFollow`は前2状態で`true`、`notFound`で`false`とする。

## 14. GCMZDrops複合操作

1. read sectionで投入前snapshot、revision、view revisionを取得する。
2. edit sectionでカーソルを対象frameへ設定する。GCMZDrops JSONの`layer`は0を使わず明示指定する。
3. SDK lockを完全に解放し、read sectionでカーソルとview revisionを再確認する。すでに変化していれば外部送信前に中止する。
4. `GCMZDropsMutex`を有限時間で取得する。
5. `GCMZDrops` FMOをコピーし、API v3、project、HWNDを検証する。
6. `GetWindowThreadProcessId`でFMO HWNDが選択AviUtl2 PIDに属することを確認する。
7. `SendMessageTimeoutW`で `WM_COPYDATA` / `dwData=2` / UTF-8 JSONを送る。
8. Mutexを解放する。
9. SDK readを有限回pollし、alias/effect/path/layer/frameで生成物を照合する。計画位置だけでなく周辺の新規生成物も確認する。
10. 計画位置で一意に検出できた場合だけ成功とし、誤配置・複数候補は生成物を添えた `partial_operation`、未確認は `operation_timeout` とする。

Mutex保持中にSDK sectionへ入らない。`WAIT_ABANDONED`、PID不一致、API不一致では送信しない。SendMessage成功だけで操作成功にしない。Command gateはMCP要求同士だけを直列化し、手動UI操作との競合を防げないため、事後検証で誤配置を成功扱いしない。

## 15. SDKスレッドと寿命

- pipe I/OとJSON parseはworker threadで行う。
- `call_read_section_param`はCommand gate workerから呼び、callback内で短い列挙と所有DTOへのコピーだけを行う。
- `call_edit_section_param`はCommand gate workerから呼び、SDKがmain threadで実行するcallback内に短い検証・更新だけを置く。
- SDK lock内でpipe/file I/O、JSON encode、PNG encode、Mutex待ち、WM_COPYDATA、render待ちを行わない。
- event listenerからSDK sectionを呼ばず、atomic revision/invalidationだけを更新する。
- 内容編集成功時はCommand gateを解放する前にcontent revision counterを同期更新し、event listener到着前でも次の旧revision編集を拒否する。
- `EDIT_SECTION*`、`PROJECT_FILE*`、OBJECT/EFFECT handle、返却文字列、render bufferをcallback外へ保持しない。
- render bufferはcallback内でpitchを検証して所有RGBAへコピーし、その後WIC workerでPNG化する。

## 16. Shutdown

1. `stopping=true`にして新規Requestを拒否する。
2. instance descriptorを削除し、新規接続を停止する。
3. queue中要求を取消し、実行中編集は安全に完了させる。
4. `CancelIoEx`後にoverlapped completionを回収する。
5. 新規renderを止め、SDK lock外でrender taskをdrainする。
6. 有限timeoutのGCMZDrops送信を完了させる。
7. workerをjoinし、pipe/event/bufferを解放する。

`FlushFileBuffers`による無期限待機と、`DllMain`内のthread join・SDK callを行わない。

## 17. Contract tests

- 40-byte header golden vectors
- UUID byte orderとlittle endian
- C++ encode -> C# decode、C# encode -> C++ decode
- 1 byte単位のfragmented read
- 複数frame連結、out-of-order response
- header/JSON/binary途中切断
- 無効magic/version/flags/UTF-8
- integer overflowと上限超過
- request ID再送、payload conflict、response消失
- request ID失効、dedup table満杯、bridge epoch変更
- 完全response cache失効後のtombstone再送
- cancelとcommit point競合
- CancelAck 3状態と元Responseの一意性
- stale descriptor、PID再利用、複数AviUtl2
- wrong GCMZDrops HWND/PID、Mutex/Send timeout、部分生成
- GCMZDrops送信直前・処理中の手動カーソル移動と誤配置検出
- timeout後のlate render callback
- shutdown中のconnect/read/write/render
