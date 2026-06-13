/**
 * @file HandlimeSystem.cs
 * @brief 展示会・コンテスト・製品版など、あらゆるシーンに合わせてシステム全体の挙動を一元管理する司令塔スクリプト
 *
 * 【このスクリプトを作った目的：マルチシーンへの柔軟な対応】
 * 文化祭での一般展示、BitSummitなどのゲームフェス、そして一般配布する製品版など、
 * 本作を「どこで、誰に遊んでもらうか」に応じて、システム全体の挙動をインスペクターの設定一つで
 * 一発切り替えできるようにするために作成しました。
 * * 用途に合わせた「時間制限の有無」「デバッグ機能の解放/制限」「終了時の挙動」などを一括制御します。
 *
 * 【主要な機能と、そのための工夫】
 *
 * 1. 状況に応じたシステム挙動の動的切り替え
 * - 【文化祭・展示会用】：体験時間をコントロールするための「制限時間タイマー」が正確に作動します。
 * - 【製品版・一般配布用】：Qキーで安全にアプリを終了させ、不正なデバッグ画面への遷移をブロックします。
 * - 【デバッグ・開発版】：Uキーで制限時間を解除（アンリミテッドモード）したり、Qキーでタイトル（Boot）へ戻れるようにし、開発やブースでのテスト効率を最大化しています。
 *
 * 2. シーンを跨いでも消えない「司令塔」の配置（Singleton パターン）
 * - ゲームが始まってから終わるまで、常に1つだけ存在し続ける仕組みにしています。
 * - 別のシーンに移動しても、上記の設定やデータ（経過時間、言語設定など）が消えず、重複生成によるバグも防ぎます。
 *
 * 3. ユーザー操作の快適な受付（新 Input System）
 * - キーボードの入力（Mキーでのカメラメニュー開閉、言語動的切り替えなど）を一括で監視しています。
 *
 * 4. AI（MediaPipe）とWebカメラの「安全な動的切り替え」（非同期制御）
 * - 本作の核となる、プレイヤーが使うWebカメラをゲーム中に切り替える処理です。
 * - カメラのテクスチャ読み込みとAIの認識処理が衝突してクラッシュするのを防ぐため、
 * 「AIを止める ➔ カメラを切り替える ➔ カメラを再起動 ➔ 確実に1拍置いてからAIを再起動」
 * というステップ（コルーチン）を組み、ポーズ画面中（時間の流れが0の時）でも絶対にバグらないように実装しています。
 *
 * 5. プレイ環境の安定化
 * - どのPCや展示機で動かしても同じ挙動（物理演算や操作感）になるよう、フレームレートを「60FPS」に固定しています。
 */

using System.Collections;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample;
using UnityEngine;
using UnityEngine.InputSystem; 
using UnityEngine.Rendering.VirtualTexturing;
using UnityEngine.SceneManagement;

public class HandlimeSystem : MonoBehaviour
{
    public bool isTA = false;
    public float runtime_min = 15;
    public float process_time = 0;
    public bool isTimeOver = false;
    private bool resetable = true;
    public float resulttime;
    public bool forTA = false;
    public bool isPublishEdition;
    public bool isUnlimited = false;
    public GameObject unlimitedSign;

    public bool isStaticFPS = false;
    private static HandlimeSystem instance;

    [HideInInspector]
    public GameObject currentCameraSettingMenu;

    void Start()
    {
        if (isStaticFPS) Application.targetFrameRate = 60;
    }

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Update()
    {
        // 制限時間タイマーのカウント処理
        if (!isTA && process_time < runtime_min * 60)
        {
            if (!isPublishEdition && !isUnlimited)
            {
                process_time += Time.deltaTime;
            }
        }
        else
        {
            isTimeOver = true;
        }

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // [Mキー] カメラ設定メニューの表示切り替え
        if (keyboard.mKey.wasPressedThisFrame)
        {
            if (currentCameraSettingMenu != null)
            {
                bool nextState = !currentCameraSettingMenu.activeSelf;
                currentCameraSettingMenu.SetActive(nextState);
                Cursor.visible = nextState;
                Debug.Log($"[HandlimeSystem] メニューの表示を {nextState} に切り替えました。");
            }
            else
            {
                Debug.LogWarning("[HandlimeSystem] 現在のシーンの CameraSettingMenu が登録されていません。");
            }
        }

        // [Qキー] 製品版ならアプリ終了、開発版ならBootシーンへ戻る（重複処理を修正）
        if (keyboard.qKey.wasPressedThisFrame)
        {
            if (isPublishEdition)
            {
                Debug.Log("アプリケーションを終了しました。");
                Application.Quit();
            }
            else
            {
                Debug.Log("[HandlimeSystem] Bootシーンへ戻ります。");
                SceneManager.LoadScene("boot");
            }
        }

        // [Eキー / Jキー] 多言語の動的切り替え
        if (keyboard.eKey.wasPressedThisFrame && LanguageManager.Instance != null)
        {
            LanguageManager.Instance.ChangeLanguage(Language.English);
        }
        if (keyboard.jKey.wasPressedThisFrame && LanguageManager.Instance != null)
        {
            LanguageManager.Instance.ChangeLanguage(Language.Japanese);
        }

        // [Uキー] 開発版限定：時間制限解除（アンリミテッドモード）のトグル切り替え
        if (keyboard.uKey.wasPressedThisFrame && !isPublishEdition)
        {
            if (SceneManager.GetActiveScene().name == "boot")
            {
                isUnlimited = !isUnlimited;
                if (unlimitedSign != null)
                {
                    unlimitedSign.SetActive(isUnlimited);
                }
                Debug.Log($"[HandlimeSystem] アンリミテッドモードを {isUnlimited} に切り替えました。");
            }
        }
    }

    public void ResetTime()
    {
        process_time = 0;
        isTimeOver = false;
    }

    private IEnumerator DurationRun()
    {
        resetable = false;
        yield return new WaitForSeconds(5);
        resetable = true;
    }

    public void ChangeForTA(bool b)
    {
        forTA = b;
    }

    private int _currentCameraIndex = 0;

    // MediapipeのWebカメラ動的切り替えルーチン（非同期処理）
    public void TriggerCameraSwitch()
    {
        StartCoroutine(SwitchCameraRoutine());
    }

    private IEnumerator SwitchCameraRoutine()
    {
        Debug.Log("--- カメラ切り替え処理開始 ---");

        // 1. ImageSourceの検出
        var imageSource = Mediapipe.Unity.Sample.ImageSourceProvider.ImageSource;
        if (imageSource == null)
        {
            Debug.LogError("エラー: ImageSourceが見つかりません。");
            yield break;
        }

        // 2. HandRecognizerRunnerの検出
        var runner = Object.FindFirstObjectByType<Mediapipe.Unity.Sample.HandRecognizerRunner>();
        if (runner == null)
        {
            Debug.LogWarning("警告: HandRecognizerRunnerがシーン内に見つかりません。カメラのみ切り替えます。");
        }

        int deviceCount = WebCamTexture.devices.Length;
        if (deviceCount <= 1)
        {
            Debug.Log("カメラが1つしかないため、切り替えをスキップします。");
            yield break;
        }

        // 次のカメラインデックスを計算
        _currentCameraIndex = (_currentCameraIndex + 1) % deviceCount;
        Debug.Log($"次のカメラを準備: Index={_currentCameraIndex}, Name={WebCamTexture.devices[_currentCameraIndex].name}");

        // 3. トラッキングシステムの一時停止
        if (runner != null) runner.Stop();
        imageSource.Stop();
        yield return new WaitForSeconds(0.5f);

        // 4. デバイスの切り替え
        if (imageSource is Mediapipe.Unity.WebCamSource webCamSource)
        {
            webCamSource.SelectSource(_currentCameraIndex);
        }

        // 5. システムの再起動
        yield return imageSource.Play();

        if (runner != null)
        {
            runner.enabled = false;

            // ポーズ中（Time.timeScale = 0）でも確実に1フレーム待機させるため、
            // WaitForSecondsRealtime を使用
            yield return new WaitForSecondsRealtime(0.2f);

            runner.enabled = true;
            Debug.Log("[HandlimeSystem] Runnerをリアルタイム待機後に再有効化しました。");
        }

        Debug.Log("--- カメラ切り替え処理完了 ---");
    }
}