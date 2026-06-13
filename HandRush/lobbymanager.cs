/**
 * @file LobbyManager.cs
 * @brief 「チョキ」を出すだけで、裏で自動で部屋を作って対戦相手と繋いでくれる、完全自動のマッチングシステム
 *
 * 【このスクリプトを作った目的：面倒な操作をゼロに】
 * オンライン対戦でよくある「部屋の番号（JoinCode）をキーボードで打ち込む」という面倒な作業を完全に無くすために作りました。
 * カメラに向かって「チョキ」のジェスチャーをするだけで、システムが自動で対戦相手を探し、いなければ自分で部屋を立てて待つ、という一連の流れをボタン操作なしで全自動で行います。
 *
 * 【主要な機能と、バグを防ぐための工夫】
 *
 * 1. 「チョキ」から始まる全自動マッチング（部屋番号の入力は不要！）
 * - プレイヤーがチョキを出すと、このプログラムが裏で動いているUnityのネットサービス（UGS）にアクセスし、空いている部屋がないか一瞬で探します。
 * - 【空いている部屋がある場合】：その部屋に設定されている「隠しコード」をプログラムが自動で読み取って、一瞬で合流します。
 * - 【部屋がない場合】：自分が最初の1人（ホスト）になり、自動で部屋を立てて、後から来る対戦相手をスマートに待ち受けます。
 *
 * 2. 通信エラーでゲームがフリーズするのを防ぐ工夫
 * - ネットの接続や部屋探しは一瞬の待ち時間が発生するため、すべての処理を「裏で非同期に（ゲームを止めずに）」実行しています。
 * - 万が一、ネット回線が不安定で部屋探しに失敗した場合でも、画面がフリーズして動かなくなるような最悪のバグ（クラッシュ）を防ぎ、安全に「もう一度やり直せる状態」に戻す親切な設計にしています。
 *
 * 3. 通信のスピードと安全性の両立
 * - リアルタイムの対戦ゲームで一番大事な「通信のサクサク感（スピード）」を確保しつつ、データが改ざんされたりチートされたりしないよう、安全に暗号化された通信ルート（DTLS）を自動で選んで接続しています。
 * - これにより、プレイヤーは何も意識することなく、安全で快適なネット対戦を楽しめます。
 *
 * 4. 連打バグや、キャンセル時のすれ違いを完全に防止
 * - マッチング中にボタンを連打されて処理がバグるのを防ぐため、一度通信が始まったらボタンを自動で押せなくしています。
 * - また、途中でマッチングをキャンセルした際も、「自分が部屋を立てていたなら部屋ごと消す」「相手の部屋に入りかけていたなら自分だけ安全に抜ける」という後片付けを徹底し、ネットの接続づまりを起こさないようにしています。
 */

using System;
using System.Collections.Generic;
// (以下、既存のソースコードが続く)


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.UI;

public class LobbyManager : MonoBehaviour
{
    public static LobbyManager Instance { get; private set; }

    private Lobby _currentLobby;
    private bool _isCancelingOrLeaving = false;

    [Header("UI 参照")]
    [SerializeField] private Button _autoMatchButton; // 自動マッチング開始
    [SerializeField] private Button _cancelButton;    // マッチングキャンセル

    private async void Awake()
    {
        // シングルトンの重複防止
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        SetButtonsState(isMatching: false);
        await InitializeUnityServicesAsync();
    }

    /// <summary>
    /// Unity Gaming Services (UGS) の初期化と匿名サインイン
    /// </summary>
    private async Task InitializeUnityServicesAsync()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                await UnityServices.InitializeAsync();
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"[LobbyManager] Sign in succeeded. PlayerID: {AuthenticationService.Instance.PlayerId}");
            }
        }
        catch (AuthenticationException ex)
        {
            Debug.LogWarning($"[LobbyManager] Authentication Warning (Safe to ignore): {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LobbyManager] Unexpected Error during initialization: {ex}");
        }
    }

    // ==========================================
    // ユーザー操作 API (自動マッチング / キャンセル)
    // ==========================================

    /// <summary>
    /// ボタン1つで最適な対戦相手を自動検索し、無ければホストとして部屋を作成する
    /// </summary>
    public async void AutoJoinOrHost()
    {
        // 連打防止および状態チェック
        if (_autoMatchButton != null && !_autoMatchButton.interactable) return;

        SetButtonsState(isMatching: true);

        // 既存のロビー状態があれば安全にクリーンアップ
        if (_currentLobby != null)
        {
            await LeaveLobbyAsync();
            await Task.Delay(300); // ネットワークのシャットダウン完了待ち
        }

        // 空きスロットのあるロビーを検索
        Lobby targetLobby = await FindJoinableLobbyAsync();

        if (targetLobby != null)
        {
            // 参加可能な部屋が見つかった場合
            await JoinLobbyAsync(targetLobby);
        }
        else
        {
            // 部屋が見つからなかった場合、自分がホストとなる
            await CreateLobbyAsHostAsync();
        }
    }

    public async void OnCancelClicked()
    {
        await LeaveLobbyAsync();
    }

    // ==========================================
    // 内部ロジック (検索 / ホスト作成 / 参加)
    // ==========================================

    /// <summary>
    /// バックエンドのロビー一覧から空きスロットがある部屋を1つ抽出する
    /// </summary>
    private async Task<Lobby> FindJoinableLobbyAsync()
    {
        try
        {
            var options = new QueryLobbiesOptions
            {
                Count = 1,
                Filters = new List<QueryFilter>
                {
                    // 空きスロットが0より大きい（＝入れる）ロビーをフィルタリング
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
                }
            };

            QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(options);
            return response.Results.Count > 0 ? response.Results[0] : null;
        }
        catch (LobbyServiceException e)
        {
            Debug.LogWarning($"[LobbyManager] Query Lobbies failed: {e}");
            return null;
        }
    }

    /// <summary>
    /// ホストとしてRelayサーバーを確保し、その接続キーをLobby情報に紐づけて部屋を作成する
    /// </summary>
    private async Task CreateLobbyAsHostAsync()
    {
        try
        {
            // 1. Relay セッションの割り当て (最大2名)
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(2);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            if (ServerManager.Instance != null)
            {
                ServerManager.Instance.JoinCodeDisplay(joinCode);
                ServerManager.Instance.PrintMessage("Waiting for Player...");
            }

            // 2. ネットワークトランスポート(UTP)へのRelayデータバインド
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            RelayServerEndpoint dtlsEndpoint = allocation.ServerEndpoints.FirstOrDefault(e => e.ConnectionType == "dtls");

            transport.SetRelayServerData(new RelayServerData(
                dtlsEndpoint.Host,
                (ushort)dtlsEndpoint.Port,
                allocation.AllocationIdBytes,
                allocation.ConnectionData,
                allocation.ConnectionData, // Hostは自分自身への接続データ
                allocation.Key,
                true // セキュア通信(DTLS)を有効化
            ));

            GameManager.IsNextGameOnline = true;
            NetworkManager.Singleton.StartHost();

            // 3. 他のクライアントが自動合流できるよう、LobbyのパブリックデータにJoinCodeを記録して作成
            var options = new CreateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    { "JoinCode", new DataObject(DataObject.VisibilityOptions.Public, joinCode) }
                }
            };

            _currentLobby = await LobbyService.Instance.CreateLobbyAsync("AutoLobby", 2, options);
            Debug.Log($"[LobbyManager] Successfully created Lobby: {_currentLobby.Id} with Relay Code: {joinCode}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[LobbyManager] CreateLobbyAsHost Failed: {e}");
            SetButtonsState(isMatching: false);
        }
    }

    /// <summary>
    /// 指定されたロビーに合流し、埋め込まれているRelay接続キーを抽出してクライアントとして通信を開始する
    /// </summary>
    private async Task JoinLobbyAsync(Lobby lobby)
    {
        try
        {
            if (ServerManager.Instance != null)
            {
                ServerManager.Instance.PrintMessage("Connecting...");
            }

            _currentLobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobby.Id);
            string joinCode = _currentLobby.Data["JoinCode"].Value;

            // クライアント側として Relay にセッション参加
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            RelayServerEndpoint dtlsEndpoint = joinAllocation.ServerEndpoints.FirstOrDefault(e => e.ConnectionType == "dtls");

            transport.SetRelayServerData(new RelayServerData(
                dtlsEndpoint.Host,
                (ushort)dtlsEndpoint.Port,
                joinAllocation.AllocationIdBytes,
                joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData, // クライアントはホストの接続データを指定
                joinAllocation.Key,
                true
            ));

            GameManager.IsNextGameOnline = true;
            NetworkManager.Singleton.StartClient();
            Debug.Log($"[LobbyManager] Successfully joined Lobby: {_currentLobby.Id} and started NetworkClient.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[LobbyManager] JoinLobby Failed: {e}");
            SetButtonsState(isMatching: false);
        }
    }

    /// <summary>
    /// ロビー状態およびネットワーク接続を完全に破棄し、安全にセッションから離脱する
    /// </summary>
    public async Task LeaveLobbyAsync()
    {
        if (_isCancelingOrLeaving) return;
        _isCancelingOrLeaving = true;

        if (ServerManager.Instance != null)
        {
            ServerManager.Instance.PrintMessage("Leaving lobby...");
        }

        if (_currentLobby != null)
        {
            try
            {
                if (AuthenticationService.Instance.IsSignedIn)
                {
                    string playerId = AuthenticationService.Instance.PlayerId;

                    if (_currentLobby.HostId == playerId)
                    {
                        // 自分がホストなら、部屋自体を完全に削除（クローズ）
                        await LobbyService.Instance.DeleteLobbyAsync(_currentLobby.Id);
                        Debug.Log("[LobbyManager] Lobby deleted by Host.");
                    }
                    else
                    {
                        // クライアントなら、自分だけ部屋から抜ける
                        await LobbyService.Instance.RemovePlayerAsync(_currentLobby.Id, playerId);
                        Debug.Log("[LobbyManager] Player removed from Lobby.");
                    }
                }
            }
            catch (LobbyServiceException e)
            {
                Debug.LogWarning($"[LobbyManager] LeaveLobby Services Error: {e.Reason}");
            }

            _currentLobby = null;
        }

        // Netcodeの通信自体も完全にシャットダウン
        if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost))
        {
            NetworkManager.Singleton.Shutdown();
        }

        if (ServerManager.Instance != null)
        {
            ServerManager.Instance.PrintMessage("Cancelled.");
        }

        _isCancelingOrLeaving = false;
        SetButtonsState(isMatching: false);
    }

    /// <summary>
    /// マッチング状態に応じて、UIボタンの有効・無効状態をトグル制御する（連打バグ防止）
    /// </summary>
    private void SetButtonsState(bool isMatching)
    {
        if (_autoMatchButton != null) _autoMatchButton.interactable = !isMatching;
        if (_cancelButton != null)    _cancelButton.interactable = isMatching;
    }
}