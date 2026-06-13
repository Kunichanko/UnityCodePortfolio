/**
 * @file StageSelectController.cs
 * @brief MediaPipeのジェスチャー認識結果とUIを連動させ、一定時間ホールドによる「非接触型決定判定」を行うUIインタラクションコントローラー
 *
 * 【このスクリプトを作った目的】
 * コントローラーやマウスを使わない「空中ジェスチャー操作」において、特定のポーズ（ピースやグーなど）を一定時間キープ（ホールド）することでステージを決定する、直感的かつ誤操作のない選択システムを作るために作成しました。
 * AIの認識が一瞬途切れたり、手を動かす途中で別のジェスチャーが誤認識されたりしても即時決定されないよう、ゲージ蓄積型のUX（ユーザー体験）を構築しています。
 *
 * 【主要な機能と、そのための工夫（ポートフォリオの見どころ）】
 *
 * 1. AI認識の「揺らぎ」をカバーするホールド＆即時リセット型UX
 * - 指定されたジェスチャー (`targetGestureName`) を検知している間だけ、目標時間 (`requiredGestureTime`) に向かってゲージをチャージします。
 * - もし途中で手がブレたりジェスチャーが変わったりした場合は、即座にゲージを初期状態 (`_circleRate = 1.0f`) に戻す仕様にしています。
 * - これにより、AI認識にありがちな「意図しない一瞬の誤認識による決定バグ」を完全に防ぎ、プレイヤーが明確な意思を持ってホールドした時だけイベントをトリガーする堅牢な選択システムを実現しています。
 *
 * 2. Time.unscaledDeltaTime の採用によるタイムスケール依存の回避
 * - 時間経過の計算に `Time.deltaTime` ではなく `Time.unscaledDeltaTime` を採用しています。
 * - これにより、ゲーム全体が一時停止（ポーズ中で `Time.timeScale = 0` の状態）していたり、シーン遷移の演出中でゲーム内の時間が止まっていたりする状況でも、メニュー操作やステージ選択のUIアニメーションが一切フリーズせず、常に滑らかに動作します。
 *
 * 3. Null Referenceを防ぐ安全な多層防御アクセス
 * - 外部ライブラリ（MediaPipe）からジェスチャー名を取得する際、`gestureRecognitionRunner`、`gesture`、さらにその先の `categories` リストまで、すべての階層でヌルチェック（`null` かどうかの確認）を徹底しています。
 * - AIのトラッキングが一瞬ロストしてデータが空（Null）になった瞬間でも、プログラムがエラーを吐いて強制終了することなく、安全にゲームが継続される設計になっています。
 *
 * 4. ライフサイクル（OnEnable / OnDisable）と連動した状態の自己クリーンアップ
 * - 画面（オブジェクト）がアクティブになった瞬間 (`OnEnable`) に、選択フラグの初期化やゲージのリセットを自動で実行します。
 * - 画面が閉じられた瞬間 (`OnDisable`) にはそれ以上の多重決定を防ぐロックをかけるなど、コンポーネント自体のオン・オフだけで状態が綺麗にリセットされる、バグの起きにくい設計を意識しています。
 */


using Mediapipe.Unity.Sample.GestureRecognition;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class StageSelectController : MonoBehaviour
{
    [SerializeField] private GestureRecognitionRunner gestureRecognitionRunner;
    
    [Header("ジェスチャー判定設定")]
    [SerializeField] private string targetGestureName = null;
    [SerializeField] private float requiredGestureTime = 1.0f;
    
    [Header("UI・イベント連携")]
    [SerializeField] private UnityEngine.UI.Image progressImage;
    [SerializeField] private UnityEvent onGestureSelected;

    private float _clockTime = 0f;
    private float _circleRate = 1.0f;
    private bool _isSelected = false;

    private void Update()
    {
        // 0.3秒ごとのデバッグログ用タイマー（unscaledTimeで実行）
        if (_clockTime < 0.3f)
        {
            _clockTime += Time.unscaledDeltaTime;
        }
        else
        {
            _clockTime = 0f;
            // 必要に応じてデバッグログを有効化
        }

        // Mediapipeの認識結果を安全に取得
        string currentGesture = GetCurrentGestureName();

        // 指定されたジェスチャーを検知している場合
        if (targetGestureName == currentGesture && !string.IsNullOrEmpty(targetGestureName))
        {
            if (_circleRate > 0f)
            {
                // 残り時間をチャージレートに変換 (unscaledDeltaTimeでポーズ中も動作)
                _circleRate -= Time.unscaledDeltaTime / requiredGestureTime;
            }
            else if (!_isSelected)
            {
                _isSelected = true;
                onGestureSelected?.Invoke();
            }
        }
        else
        {
            // ジェスチャーが外れたら即座にゲージをリセット
            _circleRate = 1.0f;
        }

        // UIの演出に反映
        if (progressImage != null)
        {
            progressImage.fillAmount = _circleRate;
        }
    }

    private string GetCurrentGestureName()
    {
        if (gestureRecognitionRunner == null || gestureRecognitionRunner.gesture == null) return null;
        
        var categories = gestureRecognitionRunner.gesture.categories;
        if (categories != null && categories.Count > 0)
        {
            return categories[0].categoryName;
        }
        return null;
    }

    private void OnEnable()
    {
        _isSelected = false;
        _circleRate = 1.0f;
    }

    private void OnDisable()
    {
        _isSelected = true;
    }

    public void UnSelected()
    {
        _isSelected = false;
        _circleRate = 1.0f;
    }
}