/**
 * @file ShakeDetector.cs
 * @brief カメラ視点を基準としたオブジェクトの「振る動作（シェイク）」を数学的に検知し、インタラクティブなイベントを発火させる角速度センサーシミュレータ
 *
 * 【このスクリプトを作った目的】
 * プレイヤーがペンライトなどのオブジェクトを「右に素早く振った」「左に振った」という直感的なアクションを、
 * 3D空間の絶対座標ではなく、常に「画面（カメラ）からどう見えているか」をベースに正確に判定するために作成しました。
 * 先述の `MaterialColorController.cs` と連携し、振る速度や方向に応じたダイナミックなライブ演出（発光色変更やパーティクル）をトリガーします。
 *
 * 【主要な機能と、そのための工夫（ポートフォリオの見どころ）】
 *
 * 1. カメラ空間（ビュー空間）へのベクトル変換による正確な画面内判定
 * - 世界の絶対的な向き（ワールド座標）で回転を計算すると、カメラの向きが変わった時に「画面内での振る方向」とズレが生じてしまいます。
 * - 本スクリプトでは、対象オブジェクトの上方向ベクトル (`target.up`) を、`worldToCameraMatrix.MultiplyVector` を用いて「カメラから見た相対的なベクトル」へと一瞬で数学的変換をかけています。
 * - これにより、カメラがどこを向いていても、画面上で「右に振られた」「左に振られた」というプレイヤーの視覚通りの直感的な検知を実現しています。
 *
 * 2. 三角関数（Mathf.Atan2）と DeltaAngle による安定した角速度計算
 * - 変換したビュー空間のX-Y平面ベクトルから `Mathf.Atan2` を使って現在の画面上の角度を割り出し、前フレームからの差分を `Mathf.DeltaAngle` で計算しています。
 * - 角度が 180度 から -180度 に切り替わる瞬間に計算がバグる（不連続になる）Unity特有の現象を `Mathf.DeltaAngle` で完全にケアし、常に滑らかで正確な角速度 (`angularSpeed`) を算出しています。
 *
 * 3. チャタリング（連続検知バグ）を防ぐクールダウン機構
 * - 一度シェイクを検知した直後、勢い余った逆方向へのブレなどでイベントが何度も連続発火（チャタリング）するのを防ぐため、コルーチン (`DurationRoutine`) による制御（フラグ管理と待機時間設定）を取り入れています。
 *
 * 4. コライダーと連動した有効エリア制限
 * - `isUseCollider` フラグを有効にすることで、特定のエリア（ライブステージの前など）にプレイヤーがいる時だけシェイク入力を受け付けるよう制限でき、誤作動を防ぐ実用的な設計にしています。
 */


using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class ShakeDetector : MonoBehaviour
{
    [Header("追跡対象とカメラ設定")]
    [SerializeField] private Transform target = null;
    [SerializeField] private Camera targetCamera;
    [SerializeField] private bool isDebug = false;

    [Header("シェイク感度設定")]
    public float angularSpeed; // 度 / 秒
    [SerializeField] private float angularExceed = 10f;
    [SerializeField] private float angularBelow = -10f;
    [SerializeField] private float durationTime = 0.5f;

    [Header("イベント連携")]
    [SerializeField] private UnityEvent exceedFunction = null;
    [SerializeField] private UnityEvent belowFunction = null;

    [Header("範囲制限・コライダー設定")]
    [SerializeField] private bool isUseCollider = false;
    private bool _inRange = false;
    private bool _isShakable = true;

    [Header("アニメーション連携")]
    [SerializeField] private List<Animator> animators = new List<Animator>();
    [SerializeField] private List<int> gimmickAnimNums = new List<int>();
    [SerializeField] private List<string> animBooleans = new List<string>();

    [Header("デバッグ設定")]
    [SerializeField] private bool rotateDebug = false;

    private float _prevZ;

    private void Start()
    {
        _prevZ = 0f;
        _isShakable = true;

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        if (rotateDebug)
        {
            StartCoroutine(RotateLogRoutine());
        }

        // 初期アニメーション状態の設定
        for (int i = 0; i < animators.Count; i++)
        {
            if (animators[i] != null && gimmickAnimNums.Count > i)
            {
                animators[i].SetInteger("type", gimmickAnimNums[i]);
            }
        }
    }

    private void Update()
    {
        if (target == null || targetCamera == null) return;

        // オブジェクトの上方向ベクトルを取得
        Vector3 forward = target.up;

        // カメラのビュー空間（視点基準）のベクトルに変換
        Vector3 viewDir = targetCamera.worldToCameraMatrix.MultiplyVector(forward);

        // 画面上でのX-Y平面の角度を計算（これがカメラから見た回転になる）
        float currentZ = Mathf.Atan2(viewDir.x, viewDir.y) * Mathf.Rad2Deg;

        // 前フレームからの回転差分（最短角度）を計算し、角速度を算出
        float delta = Mathf.DeltaAngle(_prevZ, currentZ);
        angularSpeed = delta / Time.deltaTime;
        _prevZ = currentZ;

        // シェイク判定の実行
        if (_isShakable && (!isUseCollider || _inRange))
        {
            // 右方向への素早いシェイクを検知
            if (angularSpeed > angularExceed)
            {
                TriggerExceedGimmick();
            }
            // 左方向への素早いシェイクを検知
            else if (angularSpeed < angularBelow)
            {
                TriggerBelowGimmick();
            }
        }
    }

    private void TriggerExceedGimmick()
    {
        if (isDebug) Debug.Log($"<color=red>[ShakeDetector] 正方向のシェイクを検知: {angularSpeed}</color>");
        
        exceedFunction?.Invoke();
        SetAnimatorBools(true);
        StartCoroutine(DurationRoutine());
    }

    private void TriggerBelowGimmick()
    {
        if (isDebug) Debug.Log($"<color=blue>[ShakeDetector] 負方向のシェイクを検知: {angularSpeed}</color>");
        
        belowFunction?.Invoke();
        SetAnimatorBools(false);
        StartCoroutine(DurationRoutine());
    }

    private void SetAnimatorBools(bool value)
    {
        for (int i = 0; i < animators.Count; i++)
        {
            if (animators[i] != null && animBooleans.Count > i && !string.IsNullOrEmpty(animBooleans[i]))
            {
                animators[i].SetBool(animBooleans[i], value);
            }
        }
    }

    private IEnumerator DurationRoutine()
    {
        _isShakable = false;
        yield return new WaitForSeconds(durationTime);
        _isShakable = true;
    }

    private IEnumerator RotateLogRoutine()
    {
        while (true)
        {
            if (target != null)
            {
                Debug.Log($"[RotationLog] {target.rotation.eulerAngles}");
            }
            yield return new WaitForSeconds(0.2f);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            _inRange = true;
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            _inRange = false;
        }
    }
}