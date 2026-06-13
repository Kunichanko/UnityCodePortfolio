# Unity Code Portfolio

Unityで開発した2タイトルのゲームから、技術的に工夫したスクリプトを抜粋したポートフォリオです。

---

## 収録タイトル

| タイトル | ジャンル | 技術キーワード |
|---|---|---|
| **Handlime** | ハンドジェスチャー体験型ゲーム | MediaPipe / 非同期処理 / Physics |
| **HandRush** | じゃんけんオンライン対戦ゲーム | Unity Netcode / UGS Relay & Lobby / ServerRpc / ClientRpc |

---

## Handlime

カメラに映る手をAI（MediaPipe）でリアルタイム認識し、ジェスチャーでゲームを操作する体験型タイトルです。  
展示会・ゲームフェス・一般配布など、**用途に応じてインスペクターの設定一つで挙動を切り替えられる**設計になっています。

### スクリプト一覧

---

#### `HandlimeSystem.cs` — システム全体の司令塔

**目的：** 文化祭・BitSummit・製品版など、あらゆる用途に対応できるシステム管理を1スクリプトに集約する。

| 機能 | 実装の工夫 |
|---|---|
| 展示モードの動的切り替え | インスペクターのフラグ（`isPublishEdition` / `isUnlimited`）で、タイマー制限・デバッグ機能・終了挙動を一括制御 |
| シーン跨ぎの状態保持 | `Singleton + DontDestroyOnLoad` で重複生成を防ぎつつシーン間のデータを永続化 |
| WebカメラとAIの安全な切り替え | 「AI停止 → カメラ切替 → カメラ再起動 → `WaitForSecondsRealtime` でAI再起動」をコルーチンで順序保証。ポーズ中（`timeScale=0`）でもバグらない設計 |
| フレームレート固定 | `Application.targetFrameRate = 60` でどの展示機でも同じ物理・操作感を保証 |

```csharp
// ポーズ中でも確実に1フレーム待機させるため WaitForSecondsRealtime を使用
yield return new WaitForSecondsRealtime(0.2f);
runner.enabled = true;
```

---

#### `LiftController.cs` — 多階層リフト制御

![Lift Demo](GIF/Lift.gif)

**目的：** インスペクターから階数・移動距離を設定するだけで動作するエレベーター。プレイヤーが「置いていかれる」物理バグを解決する。

| 機能 | 実装の工夫 |
|---|---|
| プレイヤーの正確な追従 | 親子関係を汚さず、リフトの**移動差分（`movementDelta`）**を毎フレーム乗客座標に加算 |
| ガタつき（ハンティング）の防止 | `FixedUpdate` 内で `Vector3.MoveTowards` を使い、目標座標を絶対に通り過ぎない移動を実現 |
| 乗客の安全な管理 | `HashSet<Transform>` で重複登録を防ぎ、`OnTriggerEnter/Exit` で出入りを管理 |
| 範囲外命令のガード | 存在しない階への命令はアニメーションも含めて即時ブロック |

```csharp
// MoveTowards でガタつきを100%回避
transform.position = Vector3.MoveTowards(currentPos, _nextPos, speed * Time.fixedDeltaTime);

// 乗客をリフトの移動量に追従させる
Vector3 movementDelta = transform.position - _prevPosition;
foreach (Transform passenger in _passengers)
    passenger.position += movementDelta;
```

---

#### `MaterialColorController.cs` — ペンライト発光・色合わせギミック

![PenLight Demo](GIF/PenLight.gif)

**目的：** ライブ演出のペンライト色変更と、ツマミで色を合わせるインタラクティブパズルを、描画負荷を上げずに実現する。

| 機能 | 実装の工夫 |
|---|---|
| メモリリーク防止と高速描画 | `MaterialPropertyBlock` を使用し、マテリアルのインスタンス化（複製）を回避してドローコールを最小化 |
| シェーダープロパティのキャッシュ | `Awake` で `Shader.PropertyToID` により文字列検索をID化し、毎フレームのCPU負荷を削減 |
| 色合わせゲームロジック | `Unity.Mathematics.math.abs` でRGB各値の誤差を判定。許容値 `colorDifference` 以内に入ったらクリア |
| 演出の疎結合化 | `UnityEvent` と `ParticleSystem` を活用し、コアロジックと演出を分離 |

```csharp
// Awake で事前ID化して実行速度を最大化
foreach (var t in targets)
    t.propertyId = Shader.PropertyToID(t.colorPropertyName);

// PropertyBlock でメモリに優しく色を書き換える
t.targetRenderer.GetPropertyBlock(_propertyBlock);
_propertyBlock.SetColor(t.propertyId, color);
t.targetRenderer.SetPropertyBlock(_propertyBlock);
```

---

#### `ShakeDetector.cs` — カメラ視点基準のシェイク検知

**目的：** ペンライトを「右に振った」「左に振った」という動作を、カメラの向きに関わらず画面上の視覚通りに正確に判定する。

| 機能 | 実装の工夫 |
|---|---|
| カメラ空間への変換 | `worldToCameraMatrix.MultiplyVector` でオブジェクトの向きをビュー空間ベクトルに変換。カメラがどこを向いても「画面上での方向」が正確に取れる |
| 滑らかな角速度計算 | `Mathf.Atan2` で現在角度を算出し、`Mathf.DeltaAngle` で±180度の不連続バグを完全に排除 |
| チャタリング防止 | シェイク検知後にコルーチンでクールダウン期間を設け、勢い余った逆方向への誤検知を防止 |
| 有効エリア制限 | `isUseCollider` フラグと `OnTriggerEnter/Exit` でステージ内にいる時だけ入力受付 |

```csharp
// オブジェクトの向きをカメラ視点ベクトルへ変換
Vector3 viewDir = targetCamera.worldToCameraMatrix.MultiplyVector(target.up);

// Atan2 で角度を求め、DeltaAngle で±180境界のバグを回避
float currentZ = Mathf.Atan2(viewDir.x, viewDir.y) * Mathf.Rad2Deg;
float delta = Mathf.DeltaAngle(_prevZ, currentZ);
angularSpeed = delta / Time.deltaTime;
```

---

#### `StageSelectController.cs` — ジェスチャーホールド選択UI

**目的：** AIの一瞬の誤認識で即決定されない、ゲージ蓄積型の非接触ステージ選択UIを実現する。

| 機能 | 実装の工夫 |
|---|---|
| ホールド＆即時リセット型UX | 指定ジェスチャーを保持中のみゲージをチャージ。手がブレた瞬間に `_circleRate = 1.0f` でリセット |
| ポーズ中でも動作するUI | `Time.unscaledDeltaTime` を採用し、`timeScale=0` の状態でもゲージアニメーションが止まらない |
| 多層ヌルチェック | MediaPipeの認識結果をrunner → gesture → categoriesの全階層でnull確認し、トラッキングロスト時のクラッシュを防止 |
| 状態の自己クリーンアップ | `OnEnable` で初期化、`OnDisable` で多重決定をロックするライフサイクル設計 |

```csharp
// timeScale=0のポーズ中でもUIが止まらない
_circleRate -= Time.unscaledDeltaTime / requiredGestureTime;

// ジェスチャーが外れた瞬間にゲージを即リセット
else { _circleRate = 1.0f; }
```

---

## HandRush

「チョキ」のジェスチャーをカメラに向けるだけで自動マッチングが始まる、じゃんけん要素のあるオンライン対戦ゲームです。  
Unity Netcode for GameObjects と Unity Gaming Services（UGS）を組み合わせ、**ボタン操作なしの全自動マッチング**を実現しています。

### スクリプト一覧

---

#### `LobbyManager.cs` — 全自動マッチングシステム

**目的：** 「部屋番号の入力」をなくし、ジェスチャー一発で対戦相手と繋がる体験を作る。

| 機能 | 実装の工夫 |
|---|---|
| 全自動Join or Host | UGS Lobbyで空き部屋を検索 → あれば参加、なければ自分がホストとして部屋を作成 |
| JoinCodeの自動共有 | ホスト作成時にRelayのJoinCodeをLobbyのパブリックデータに埋め込み、クライアントが自動取得して接続 |
| DTLS暗号化通信 | `ServerEndpoints` からDTLSエンドポイントを抽出してUTPにバインドし、セキュアな通信を確保 |
| キャンセル時の後片付け | ホストなら部屋を削除、クライアントなら自分だけ退出と役割を分けて、接続詰まりを防止 |
| 連打バグ防止 | マッチング中はボタンを `interactable = false` にしてロック |

```csharp
// 空きスロットがある部屋だけフィルタリング
new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)

// JoinCodeをLobbyのパブリックデータとして公開
{ "JoinCode", new DataObject(DataObject.VisibilityOptions.Public, joinCode) }
```

---

#### `MaterialManager.cs` — トロッコ外観リアルタイム更新

**目的：** じゃんけんの属性とパワーチャージ量を、トロッコの発光・マークに視覚的にリアルタイム反映する。

| 機能 | 実装の工夫 |
|---|---|
| じゃんけんタイプによる一括外観変更 | シェーダープロパティにInt/Floatを直接送ることで、複数オブジェクトの見た目を同期して切り替え |
| チャージ量の滑らかな反映 | 毎フレームチャージ量（0〜1）を `_chargeMaxAmount` で乗算してマテリアルに送り続ける |
| 異常値の安全なクランプ | 外部からセットされる値を `Mathf.Clamp01` で0〜1に強制丸めし、描画バグを未然に防止 |
| Nullガード | リスト内の未設定データはすべてスキップし、設定ミスによるクラッシュを防止 |

```csharp
// 外部からの異常値を安全な範囲に丸める
_chargeAmount = Mathf.Clamp01(amount);
```

---

#### `ServerManager.cs` — マルチプレイ通信管制

**目的：** 2人のプレイヤー画面でキャラクター位置・スコアがズレる「同期ズレ」を、Server権威モデルで完全に防ぐ。

| 機能 | 実装の工夫 |
|---|---|
| プレイヤーロールの自動割り当て | 接続順を `Dictionary<ulong, PlayerRole>` で管理し、1P・2Pを自動で割り当て。2人揃った瞬間にゲーム開始 |
| サーバー時刻による同時スタート | `NetworkManager.Singleton.ServerTime.Time + 1.0` の未来時刻を全クライアントに送り、通信ラグを排除した完全同時スタートを実現 |
| ServerRpc → ClientRpc の権威モデル | 兵士召喚・トロッコ移動・マテリアル変更はすべてServerRpcを経由させ、サーバーが計算した結果をClientRpcで全員に配布 |
| スコアの完全同期 | `SyncScoreClientRpc` でスコア変動のたびに全クライアントへ最新値を配布 |
| macOS Wi-Fi対応 | `NetworkInterfaceType.Ethernet` も許可することで、Wi-FiをEthernetとして返すmacOSの仕様に対応 |
| 切断・キャンセル時のクリーンアップ | `OnNetworkDespawn` でroleMap・SoldierID・NetworkListを完全リセットし、次回起動時の残留データバグを防止 |

```csharp
// サーバー時刻基準で全クライアントが同時スタート
double startTime = NetworkManager.Singleton.ServerTime.Time + 1.0;
GameStartClientRpc(startTime, GameManager.IsSpecialRuleEnabled);

// macOSでWi-FiがEthernetとして返ることへの対応
bool isTargetType = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
```

---

## 技術スタック

- **Engine:** Unity 2022〜2023
- **言語:** C#
- **AI:** MediaPipe (Hand Landmark / Gesture Recognition)
- **ネットワーク:** Unity Netcode for GameObjects
- **オンラインサービス:** Unity Gaming Services — Lobby / Relay / Authentication
- **物理・UI:** Unity New Input System / Unity Mathematics / TextMeshPro
