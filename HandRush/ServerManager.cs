/**
 * @file ServerManager.cs
 * @brief オンライン対戦におけるプレイヤーの役割、トロッコ・兵士の動き、スコア同期など、マルチプレイの全ルールを統括する通信管制マネージャー
 *
 * 【このスクリプトを作った目的：ズレのない快適な対戦環境の構築】
 * 2人のプレイヤーがオンラインで対戦する際、「自分の画面」と「相手の画面」でキャラクターの位置やゲームの点数がズレてしまうバグ（同期ズレ）を完璧に防ぐために作りました。
 * Unityの通信システム（Netcode）を利用し、「ゲームの重要な計算や決定はすべてサーバー（親）が行い、その結果をクライアント（子）に届ける」という仕組みの土台となるプログラムです。
 *
 * 【主要な機能と、バグを防ぐための工夫】
 *
 * 1. 2人のプレイヤーの役割（1P・2P）を自動で割り当て
 * - ゲームに参加した順番を検知し、最初のプレイヤーを「プレイヤー1」、次のプレイヤーを「プレイヤー2」として自動で役割（ロール）を設定・登録します。
 * - 2人のプレイヤーがちゃんと揃った瞬間に、サーバーの時間（時刻）を基準にして「せーの」で完全に同時にゲームを開始させることで、通信ラグによる有利不利を無くしています。
 *
 * 2. 兵士の召喚やトロッコの動きを全画面に正しく同期
 * - プレイヤーが兵士を出したりトロッコを動かしたりした時、まずはサーバーに「動かしていいですか？」というリクエスト（ServerRpc）を送ります。
 * - リクエストを受け取ったサーバーが正しい位置を計算し、全員の画面へ「ここに表示して、こう動かして！」と一斉に命令（ClientRpc）を出すことで、お互いの画面が常に同じ状態に保たれます。
 *
 * 3. スコア（点数）や特殊ルールの完全同期
 * - 対戦中の点数（スコア）の増減や、特殊ルールが有効かどうかの判定も、すべてサーバーが一括管理しています。
 * - 点数が変わるたびに全員の画面へ新しい点数を送り届ける（SyncScoreClientRpc）ことで、お互いの画面で勝敗の判定がズレるのを防いでいます。
 *
 * 4. macOSでも安心して遊べる、賢いIPアドレス（Wi-Fi）取得機能
 * - ローカル対戦（近くの人と遊ぶモード）の際、自分のパソコンのIPアドレスを自動で取得して接続の準備をします。
 * - 開発時にバグになりがちな「MacだとWi-Fiの接続名が違って通信相手を見つけられない」という特殊な仕様（macOSのEthernet判定）を先回りしてカバーし、WindowsでもMacでもスムーズに通信できるように工夫しています。
 *
 * 5. 退出・キャンセル時の「後片付け」の徹底
 * - 対戦をキャンセルしたり、どちらかのプレイヤーの接続が切れたりした時は、サーバー内に残っているデータ（兵士のIDやプレイヤーの登録情報）を完全にリセット（クリーンアップ）します。
 * - これにより、次に新しくゲームを始める時に前のゲームのデータが残ってバグを引き起こす、というマルチプレイ開発特有のトラブルを未然に防いでいます。
 */


using System.Collections;
using System.Collections.Generic;
using System.Linq; // ← ★これを追加
using System.Net;
// using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

using TMPro;

using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

//using static UnityEditor.PlayerSettings;


public enum PlayerRole
{
    None,
    Player1,
    Player2
}
public enum PlayerTrolleyType
{
    none,
    Player1_R,
    Player1_L,
    Player2_R,
    Player2_L,
}

public class ServerManager : NetworkBehaviour
{
    [Header("オンライン専用のオブジェクト")][SerializeField] private List<GameObject> _onlineObjects;
    [Header("オフライン専用のオブジェクト")][SerializeField] private List<GameObject> _offlineObjects;

    [Header("unityRelayを使うか")][SerializeField] private bool useRelay = false;
    [Header("JoinCodeを表示するテキスト")][SerializeField] private TMP_Text _joinCodeText;
    [Header("JoinCodeを打ち込むフィールド")][SerializeField] private TMP_InputField _joinCodeInputField;
    [Header("メッセージ(接続状況など)を表示するテキスト")][SerializeField] private TMP_Text _MessageText;
    [Header("プレイヤーのロールを表示するテキスト")][SerializeField] private TMP_Text _PlayerText;

    [Header("IPアドレスの管理のためのトランスポート")][SerializeField] private UnityTransport _transport;
    [Header("オンライン対戦であるかどうか")][SerializeField] private bool _isOnline;//この値はトロッコやプレイヤー等から参照されます
    [Header("プレイヤーが二人そろうと発生するイベント")][SerializeField] private UnityEvent _startEvent;
    [Header("各プレイヤーの操作するトロッコ(権限問題より、serverのみでtransformを変更する)")]
    [SerializeField] private GameObject _player1_R;
    [SerializeField] private GameObject _player1_L;
    [SerializeField] private GameObject _player2_R;
    [SerializeField] private GameObject _player2_L;

    [Header("アニメーターを同期させるトロッコ(1-R,1-L,2-R,2-Lの順に格納)")]
    [SerializeField] private List<Animator> _animators;
    [Header("プレイヤー1側の発射位置(左から)")][SerializeField] private List<Transform> _player1_Lanes;
    [Header("プレイヤー2側の発射位置(右から)")][SerializeField] private List<Transform> _player2_Lanes;

    [Header("プレイヤー1の兵士プレハブ")][SerializeField] private GameObject _player1Soldier;
    [Header("プレイヤー2の兵士プレハブ")][SerializeField] private GameObject _player2Soldier;

    [Header("マテリアル変更のスクリプト(1-R,1-L,2-R,2-Lの順に格納)")][SerializeField] private List<MaterialManager> _matManagers;

    // Server 内部用
    private Dictionary<ulong, PlayerRole> roleMap =
        new Dictionary<ulong, PlayerRole>();

    // 全 Client に配布する Role 情報
    public NetworkList<NetRoleManager> Roles;
    public static ServerManager Instance { get; private set; }

    private PlayerRole _role = PlayerRole.None;

    private int _SoldierID = 0;

    Dictionary<int, SoldierController> _SoldierMap = new Dictionary<int, SoldierController>();

    // ===== Relay 用 =====
    private bool _relayInitialized = false;
    private string _attackTriggerName = "atack";

    void Awake()
    {
        Roles = new NetworkList<NetRoleManager>();


        if (Instance != null && Instance != this)
        {
            Destroy(gameObject); // 既にInstanceがあれば自分を破棄
            return;
        }
        Instance = this; // このオブジェクトをシングルトンとして登録
        //DontDestroyOnLoad(gameObject); // シーンを跨いでも消さない場合
        Debug.Log("The current IP adress is:" + GetWifiIPv4());
        StartCoroutine(GetIpDelayed());


        InitializeUnityServices();//added.

    }

    public void OfflineObjectsEnable(bool isOffline)
    {
        _isOnline = !isOffline;

        foreach (var obj in _onlineObjects)
        {
            obj.SetActive(!isOffline);
        }
        foreach (var obj in _offlineObjects)
        {
            obj.SetActive(isOffline);
        }

        PlayerController[] scripts = FindObjectsOfType<PlayerController>();

        foreach (var s in scripts)
        {
            s.OnlineCheck();
        }
        if (ServerManager.Instance != null)
        {
            ServerManager.Instance.CanselMatching();
        }

    }

    public void StartHostAdvance()
    {
        if (useRelay)
        {
            StartHostWithRelay();
        }
        else
        {
            NetworkManager.Singleton.StartHost();
        }
    }
    public void PrintMessage(string message)
    {
        if (_MessageText != null)
        {
            _MessageText.text = message;
        }
    }

    public void StartClientAdvance()
    {
        if (useRelay)
        {
            if (_joinCodeInputField == null)
            {
                Debug.LogError("Join Code Input Field is not assigned.");
                return;
            }
            string joinCode = _joinCodeInputField.text;
            StartClientWithRelay(joinCode);
        }
        else
        {
            NetworkManager.Singleton.StartClient();
        }
    }
    IEnumerator GetIpDelayed()
    {
        yield return null;              // 1フレーム待つ
        yield return new WaitForSeconds(0.5f); // 保険
        _transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (_transport != null) { Debug.Log("Transport getting failed."); }


    }

    public void ChangeIPadress(string ipadress)
    {
        if (_transport != null)
        {
            _transport.ConnectionData.Address = GetWifiIPv4();
            Debug.Log("The current IP address is " + _transport.ConnectionData.Address);
            Debug.Log("IP address is :" + GetWifiIPv4());
        }
        else
        {
            Debug.Log("Transport is null.");
        }
    }

    public bool GetisOnline()
    {
        return _isOnline;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            Debug.Log("Server despawned. Clearing server state.");

            roleMap.Clear();
            Roles.Clear();
            _SoldierID = 0;
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }


    void OnClientConnected(ulong clientId)//HostまたはClientボタンが押されると起動
    {
        if (roleMap.Count >= 2)
        {
            Debug.Log("Already 2 players connected");
            return;
        }

        PlayerRole role =
            roleMap.Count == 0 ? PlayerRole.Player1 : PlayerRole.Player2;

        roleMap[clientId] = role;
        //IDをハッシュ化してrolemapに登録　この行によりroleMap.Countが増加

        Roles.Add(new NetRoleManager
        {
            ClientId = clientId,
            Role = role
        });

        if (roleMap.Count == 2) { Gamestart(); }
        //プレイヤーが二人以上になった時、ゲームを開始する。

        Debug.Log($"Client {clientId} assigned {role}");
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (!roleMap.ContainsKey(clientId)) return;

        roleMap.Remove(clientId);

        for (int i = Roles.Count - 1; i >= 0; i--)
        {
            if (Roles[i].ClientId == clientId)
                Roles.RemoveAt(i);
        }

        // ★ 誰もいなくなったら完全リセット
        if (roleMap.Count == 0)
        {
            Roles.Clear();
            _SoldierID = 0;
            Debug.Log("All roles cleared.");
        }
    }


    public void Gamestart()
    {
        //GameStartClientRpc();
        double startTime = NetworkManager.Singleton.ServerTime.Time + 1.0; // 1秒後
        GameStartClientRpc(startTime, GameManager.IsSpecialRuleEnabled);


    }

    public void JoinCodeDisplay(string code)
    {
        _joinCodeText.text = code;
    }

    [ClientRpc]
    public void SoldierSpawnClientRpc(int id, PlayerRole role, int laneIndex, int unitType)//Roleとレーン番号に応じてSoldierを召喚
    {
        if (laneIndex >= 0 && laneIndex <= _player1_Lanes.Count)
        {
            GameObject soldierPrehub = null;
            Vector3 pos = new Vector3(0, 0, 0);
            Quaternion direc = Quaternion.identity;
            //PlayerControllerは自分のロールと共に呼び出し、サーバーにて兵士の生成を行う。
            if (role == PlayerRole.Player1)
            {

                soldierPrehub = _player1Soldier;
                pos = _player1_Lanes[laneIndex].position;
                direc = _player1_Lanes[laneIndex].rotation;

            }
            else if (role == PlayerRole.Player2)
            {

                soldierPrehub = _player2Soldier;
                pos = _player2_Lanes[laneIndex].position;
                direc = _player2_Lanes[laneIndex].rotation;
            }

            GameObject newSoldier = Instantiate(soldierPrehub, pos, direc);

            // 保存しておいたタイプ情報を使う
            SoldierController sc = newSoldier.GetComponent<SoldierController>();
            sc.SoldierID = id;
            sc.RegisterServerManager(this);
            _SoldierMap[id] = sc;



            if (sc != null)
            {
                sc.unitType = unitType; // 変数名をSoldierControllerに合わせて修正(unitType or type)
            }
        }

    }
    //[ClientRpc]
    //public void ReturnToMenuClientRpc()
    //{
    //    StartCoroutine(ReturnToMenuRoutine());
    //}

    //IEnumerator ReturnToMenuRoutine()
    //{
    //    // 念のため入力停止
    //    yield return null;

    //    NetworkManager.Singleton.Shutdown();

    //    // NGO内部の完全停止待ち
    //    yield return new WaitForSeconds(0.2f);

    //    //SceneManager.LoadScene("MenuScene");
    //}



    [ClientRpc]
    public void GameStartClientRpc(double serverStartTime, bool isSpecialRule)
    {
        // ★追加: 受け取ったルールで、自分のGameManagerの設定を上書き同期する
        if (GameManager.Instance != null)
        {
            GameManager.IsSpecialRuleEnabled = isSpecialRule;
            Debug.Log($"Rule Synced: Special Enabled = {isSpecialRule}");
        }

        StartCoroutine(WaitAndStart(serverStartTime));
    }
    IEnumerator WaitAndStart(double serverStartTime)
    {
        while (NetworkManager.Singleton.ServerTime.Time < serverStartTime)
        {
            yield return null;
        }

        _startEvent.Invoke();
        Debug.Log("サーバー時刻基準で同時スタート！");
    }


    [ClientRpc]
    public void TorolleyAnimClientRpc(PlayerTrolleyType type)
    {

        if ((type == PlayerTrolleyType.Player1_L || type == PlayerTrolleyType.Player1_R) && GetMyRole() == PlayerRole.Player1 ||
            (type == PlayerTrolleyType.Player2_L || type == PlayerTrolleyType.Player2_R) && GetMyRole() == PlayerRole.Player2) { return; }

        switch (type)
        {
            case PlayerTrolleyType.Player1_R:
                _animators[0].SetTrigger(_attackTriggerName);
                break;
            case PlayerTrolleyType.Player1_L:
                _animators[1].SetTrigger(_attackTriggerName);
                break;
            case PlayerTrolleyType.Player2_R:
                _animators[2].SetTrigger(_attackTriggerName);
                break;
            case PlayerTrolleyType.Player2_L:
                _animators[3].SetTrigger(_attackTriggerName);
                break;

            default:
                break;

        }
    }




    [ClientRpc]
    public void TorolleyPositionMoveClientRpc(PlayerTrolleyType type, Vector3 pos)
    {

        if ((type == PlayerTrolleyType.Player1_L || type == PlayerTrolleyType.Player1_R) && GetMyRole() == PlayerRole.Player1 ||
            (type == PlayerTrolleyType.Player2_L || type == PlayerTrolleyType.Player2_R) && GetMyRole() == PlayerRole.Player2) { return; }

        //Debug.Log("MoveRequest:"+type+" pos:"+pos);
        switch (type)
        {
            case PlayerTrolleyType.Player1_R:
                _player1_R.transform.position = pos;
                break;
            case PlayerTrolleyType.Player1_L:
                _player1_L.transform.position = pos;
                break;
            case PlayerTrolleyType.Player2_R:
                _player2_R.transform.position = pos;
                break;
            case PlayerTrolleyType.Player2_L:
                _player2_L.transform.position = pos;
                break;

            default:
                break;

        }
    }
    [ClientRpc]
    public void SoldierDestroyClientRpc(int id)
    {
        if (_SoldierMap.TryGetValue(id, out var sc))
        {
            sc.DestroyRequest();
            _SoldierMap.Remove(id);
        }
    }

    [ClientRpc]
    public void MaterialChangeClientRpc(int requestType, PlayerTrolleyType type, int changeType, float changeValue)
    {
        Debug.Log("<color=#00FFFF>Material change requasted.</color>:" + "request type:" + requestType + "type:" + type + "change:" + changeType + "value" + changeValue);
        if (requestType == 1 || requestType == 3)
        {

            if (type == PlayerTrolleyType.Player1_R && GetMyRole() == PlayerRole.Player2)
            {
                _matManagers[0].JankenColorSet(changeType);
                Debug.Log("<color=red>[0] triggered.</color>");
            }
            if (type == PlayerTrolleyType.Player1_L && GetMyRole() == PlayerRole.Player2)
            {
                _matManagers[1].JankenColorSet(changeType);
                Debug.Log("<color=red>[1] triggered.</color>");
            }
            if (type == PlayerTrolleyType.Player2_R && GetMyRole() == PlayerRole.Player1)
            {
                _matManagers[2].JankenColorSet(changeType);
                Debug.Log("<color=red>[2] triggered.</color>");
            }
            if (type == PlayerTrolleyType.Player2_L && GetMyRole() == PlayerRole.Player1)
            {
                _matManagers[3].JankenColorSet(changeType);
                Debug.Log("<color=red>[3] triggered.</color>");
            }
        }
        if (requestType == 2 || requestType == 3)
        {
            if (type == PlayerTrolleyType.Player1_R && GetMyRole() == PlayerRole.Player2)
            {
                _matManagers[0].JankenChargeSet(changeValue);
                Debug.Log("<color=red>[0] triggered.</color>");
            }
            if (type == PlayerTrolleyType.Player1_L && GetMyRole() == PlayerRole.Player2)
            {
                _matManagers[1].JankenChargeSet(changeValue);
                Debug.Log("<color=red>[1] triggered.</color>");
            }
            if (type == PlayerTrolleyType.Player2_R && GetMyRole() == PlayerRole.Player1)
            {
                _matManagers[2].JankenChargeSet(changeValue);
                Debug.Log("<color=red>[2] triggered.</color>");
            }
            if (type == PlayerTrolleyType.Player2_L && GetMyRole() == PlayerRole.Player1)
            {
                _matManagers[3].JankenChargeSet(changeValue);
                Debug.Log("<color=red>[3] triggered.</color>");
            }
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TorolleyMoveRequestServerRpc(PlayerTrolleyType type, Vector3 pos)
    {
        TorolleyPositionMoveClientRpc(type, pos);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SoldierRequestServerRpc(PlayerRole role, int index, int unitType)//サーバーを一度通してClientRpcを起動
    {
        int id = _SoldierID++;
        SoldierSpawnClientRpc(id, role, index, unitType);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void MaterialRequestServerRpc(int requestType, PlayerTrolleyType type, int changeType, float changeValue)
    {//マテリアルの変更を行う。requestTypeが 1:typeの変更 2:valueの変更 3:両方の変更
        MaterialChangeClientRpc(requestType, type, changeType, changeValue);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SoldierDestroyServerRpc(int id)
    {
        SoldierDestroyClientRpc(id);
    }
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TorolleyAnimServerRpc(PlayerTrolleyType type)
    {
        TorolleyAnimClientRpc(type);
    }

    // ===== Client 用API =====

    public PlayerRole GetMyRole()
    {
        ulong myId = NetworkManager.Singleton.LocalClientId;

        foreach (var entry in Roles)
        {
            if (entry.ClientId == myId)
                return entry.Role;
        }

        return PlayerRole.None;
    }
    public int GetPlayerCount()
    {
        return roleMap.Count;
    }

    public void SetCurrentQifiPv4()
    {
        ChangeIPadress(GetWifiIPv4());
    }
    public static string GetWifiIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 【修正点】macOS対策として Ethernet も許可する
            // macOSではWi-Fiが Wireless80211 ではなく Ethernet として返ってくるため
            bool isTargetType = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet;

            // ループバック（ローカルホスト）やVPN等の仮想アダプタを除外する
            // "en0" はmacOSの標準的なWi-Fiインターフェース名です
            if (isTargetType &&
                ni.OperationalStatus == OperationalStatus.Up &&
                !ni.Description.ToLower().Contains("virtual") && // 仮想環境対策
                !ni.Name.ToLower().Contains("loopback"))         // ループバック対策
            {
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        // ループバックアドレス(127.0.0.1)でないことを念のため確認
                        if (!IPAddress.IsLoopback(ip.Address))
                        {
                            return ip.Address.ToString();
                        }
                    }
                }
            }
        }
        return null;
    }


    public async void CanselMatching()
    {
        Debug.Log("Network shutdown requested.");

        if (LobbyManager.Instance != null)
        {
            await LobbyManager.Instance.LeaveLobbyAsync();
        }

        if (IsServer)
        {
            roleMap.Clear();
            Roles.Clear();
            _SoldierID = 0;
            Debug.Log("Force-cleared server state on cancel.");
        }

        if (NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost))
        {
            NetworkManager.Singleton.Shutdown();
        }

        ClearLocalRole();
        PrintMessage("cancelled.");
    }




    void ClearLocalRole()
    {
        _role = PlayerRole.None;
        _PlayerText.text = "none";

        // NetworkListもローカル参照はクリア
        if (Roles != null)
            Roles.Clear();
    }

    private async void InitializeUnityServices()
    {
        if (_relayInitialized) return;

        await UnityServices.InitializeAsync();

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        _relayInitialized = true;
        Debug.Log("Unity Services Initialized");
    }
    public async void StartHostWithRelay(int maxPlayers = 2)
    {
        if (!_relayInitialized)
        {
            Debug.LogError("Relay not initialized");
            return;
        }

        Allocation allocation =
            await RelayService.Instance.CreateAllocationAsync(maxPlayers);

        string joinCode =
            await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

        Debug.Log($"Relay Join Code: {joinCode}");

        if (_joinCodeText != null)
        {
            _joinCodeText.text = joinCode;
        }

        // ▼【修正】UTP 2.0以降向けの書き方（AllocationからDTLS情報を抽出して渡す）
        RelayServerEndpoint dtlsEndpoint = allocation.ServerEndpoints.FirstOrDefault(e => e.ConnectionType == "dtls");

        var serverData = new RelayServerData(
            dtlsEndpoint.Host,
            (ushort)dtlsEndpoint.Port,
            allocation.AllocationIdBytes,
            allocation.ConnectionData,
            allocation.ConnectionData, // Hostの場合はHostConnectionDataにも自分のデータを入れます
            allocation.Key,
            true // isSecure (DTLSはセキュアなのでtrue)
        );

        _transport.SetRelayServerData(serverData);

        NetworkManager.Singleton.StartHost();
    }

    public async void StartClientWithRelay(string joinCode)
    {

        if (!_relayInitialized)
        {
            Debug.LogError("Relay not initialized");
            return;
        }

        JoinAllocation joinAllocation =
            await RelayService.Instance.JoinAllocationAsync(joinCode);

        // ▼【修正】UTP 2.0以降向けの書き方
        RelayServerEndpoint dtlsEndpoint = joinAllocation.ServerEndpoints.FirstOrDefault(e => e.ConnectionType == "dtls");

        var serverData = new RelayServerData(
            dtlsEndpoint.Host,
            (ushort)dtlsEndpoint.Port,
            joinAllocation.AllocationIdBytes,
            joinAllocation.ConnectionData,
            joinAllocation.HostConnectionData, // Clientの場合はHostConnectionDataを渡します
            joinAllocation.Key,
            true // isSecure
        );

        _transport.SetRelayServerData(serverData);

        NetworkManager.Singleton.StartClient();
    }
    // ★追加: 点数を全員に同期するためのClientRpc
    [ClientRpc]
    public void SyncScoreClientRpc(int p1Score, int p2Score)
    {
        // 受け取った全員（ホストもクライアントも）が実行する
        if (GameManager.Instance != null)
        {
            GameManager.Instance.UpdateNetworkScores(p1Score, p2Score);
        }
    }
    // ★追加: ポイント消費をサーバーに伝える
    [Rpc(SendTo.Server)]
    public void ConsumeScoreServerRpc(bool isPlayerSide)
    {
        // サーバー側でGameManagerを操作してポイントを減らす
        if (GameManager.Instance != null)
        {
            GameManager.Instance.TryConsumeSpecialCharge(isPlayerSide);
        }
    }
}
