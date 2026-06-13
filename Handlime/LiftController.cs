/**
 * @file LiftController.cs
 * @brief 階層間の移動、アニメーション連動、および乗客（プレイヤー）の座標同期を安全に行う多機能リフト制御スクリプト
 *
 * 【このスクリプトを作った目的】
 * 複数フロアを行き来するエレベーター（リフト）の挙動を、インスペクターから階数や移動距離を指定するだけで手軽に実現するために作成しました。
 * 特に「動く床」で発生しやすい、プレイヤーが置いていかれたりガタついたりする物理的な不具合を解決するための工夫を凝らしています。
 *
 * 【主要な機能と、そのための工夫】
 *
 * 1. 移動量計算による「乗客（プレイヤー）の正確な追従追従処理」
 * - 単にプレイヤーをリフトの子オブジェクト（Parent）にする古典的な手法ではなく、リフト自体の移動差分 (`movementDelta`) を算出して毎フレーム乗客の座標に加算する手法を採用しています。
 * - これにより、プレイヤー側の親オブジェクトの構造を汚すことなく、リフトが動いてもプレイヤーが滑り落ちたり、置いていかれたりしない安定した追従を実現しています。
 * - 乗客の管理には重複を防ぐ `HashSet` を使い、`OnTriggerEnter/Exit` で Rigidbody やルートオブジェクトを検知して安全にリストへ出し入れしています。
 *
 * 2. 物理演算と完全に同期した滑らかな移動（FixedUpdate × MoveTowards）
 * - 物理演算の更新タイミングである `FixedUpdate` 内で、リフトの座標を更新しています。
 * - `Vector3.MoveTowards` を使用することで、目標座標を通り過ぎて往復してしまう「ガタつき（ハンティング現象）」を100%回避し、ピタッと正確にフロアに停止させます。
 *
 * 3. 堅牢なエラーハンドリング（境界チェック）
 * - 外部から「上の階へ」「指定の階へ」といった移動命令 (`ChangeFloor`, `SetFloor`) が飛んできた際、設定されたフロア範囲外（マイナス階や存在しない最上階）への移動を即座に弾くガード句を入れています。
 * - 無効な命令時には無駄なアニメーションも実行されないため、ゲーム全体の安全性が高まっています。
 *
 * 4. 演出（アニメーション）とのスムーズな連動
 * - リフトの起動時、およびフロアへの到着時（`_isMoving` が false になった瞬間）に自動で指定のAnimatorトリガーを発火させ、扉の開閉や昇降演出とプログラムの挙動を完璧に同期させています。
 */


using System.Collections.Generic;
using UnityEngine;

public class LiftController : MonoBehaviour
{
    [SerializeField] private List<bool> floorList = new List<bool>(); // 各階の有効フラグ
    [SerializeField] private int initialFloor = 0;
    [SerializeField] private float speed = 5.0f;
    [SerializeField] private float floorDistance = 20.0f;
    
    [Header("Animation Settings")]
    [SerializeField] private Animator animator;
    [SerializeField] private int animTypeNum = 0;
    [SerializeField] private string animTriggerName = "";

    // プロパティとカプセル化
    public int NowFloor { get; private set; } = 0;

    private Vector3 _initialPos;
    private Vector3 _nextPos;
    private Vector3 _prevPosition;
    private bool _isMoving;

    private readonly HashSet<Transform> _passengers = new HashSet<Transform>();

    private void Start()
    {
        NowFloor = initialFloor;
        _initialPos = transform.position;
        _prevPosition = transform.position;

        // 初期位置を現在の階数に合わせる
        _nextPos = _initialPos;
        _nextPos.y = _initialPos.y + NowFloor * floorDistance;
        transform.position = _nextPos;

        if (animator != null)
        {
            animator.SetInteger("type", animTypeNum);
        }
    }

    private void FixedUpdate()
    {
        // MoveTowards を使うことで、ガタつき（行き過ぎバグ）を100% 回避
        Vector3 currentPos = transform.position;
        transform.position = Vector3.MoveTowards(currentPos, _nextPos, speed * Time.fixedDeltaTime);

        // 目的地に到着した瞬間の処理
        if (_isMoving && transform.position == _nextPos)
        {
            if (animator != null && !string.IsNullOrEmpty(animTriggerName))
            {
                animator.SetTrigger(animTriggerName);
            }
            _isMoving = false;
        }

        // 乗客（Player）をリフトの移動量に合わせて同期移動させる
        Vector3 movementDelta = transform.position - _prevPosition;
        if (movementDelta != Vector3.zero)
        {
            foreach (Transform passenger in _passengers)
            {
                if (passenger != null)
                {
                    passenger.position += movementDelta;
                }
            }
        }

        _prevPosition = transform.position;
    }

    public void ChangeFloor(int count)
    {
        int targetFloor = NowFloor + count;

        // 階数の限界チェック（範囲外ならアニメーションも走らせずに終了）
        if (targetFloor < 0 || targetFloor >= floorList.Count) return;

        NowFloor = targetFloor;
        _nextPos.y = _initialPos.y + NowFloor * floorDistance;
        _isMoving = true;

        if (animator != null && !string.IsNullOrEmpty(animTriggerName))
        {
            animator.SetTrigger(animTriggerName);
        }
    }

    public void BooleanChangeFloor(bool isUp)
    {
        ChangeFloor(isUp ? 1 : -1);
    }

    public void SetFloor(int floorNum)
    {
        if (floorNum < 0 || floorNum >= floorList.Count) return;

        NowFloor = floorNum;
        _nextPos.y = _initialPos.y + NowFloor * floorDistance;
        _isMoving = true;

        if (animator != null && !string.IsNullOrEmpty(animTriggerName))
        {
            animator.SetTrigger(animTriggerName);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        Transform rootTransform = other.transform.root;

        if (rb != null && (rb.CompareTag("parent") || rb.name == "Player"))
        {
            _passengers.Add(rb.transform);
        }
        else if (rootTransform.CompareTag("parent") || rootTransform.name == "Player")
        {
            _passengers.Add(rootTransform);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;

        if (rb != null)
        {
            _passengers.Remove(rb.transform);
        }
        else
        {
            _passengers.Remove(other.transform.root);
        }
    }
}