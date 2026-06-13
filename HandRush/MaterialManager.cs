/**
 * @file MaterialManager.cs
 * @brief じゃんけんの属性（グー・チョキ・パー）や、パワーのチャージ量に合わせて「トロッコの見た目」をリアルタイムに変える演出マネージャー
 *
 * 【このスクリプトを作った目的：視覚的なワクワク感と分かりやすさの提供】
 * プレイヤーの手の動きに合わせて、ゲーム内のトロッコの色やマークを「グー・チョキ・パー」の属性へ一瞬で切り替えたり、
 * パワーが溜まっていく様子（チャージ量）に合わせて、見た目の輝きやエフェクトをリアルタイムに変化させるために作成しました。
 * プレイヤーに「今、自分の属性が何になっていて、どれくらいパワーが溜まっているか」を、画面を見て直感的に分からせるための大事な架け橋です。
 *
 * 【主要な機能と、バグを防ぐための工夫】
 *
 * 1. じゃんけんのタイプに合わせて見た目を一括チェンジ
 * - 外部から「今はグー（1）」「今はチョキ（2）」という情報を受け取ると、あらかじめ登録しておいたマテリアルリスト（見た目の設定データ）をすべて自動でスキャンします。
 * - シェーダー（マテリアルの裏側のプログラム）に対して直接数字を送り届けることで、複数のオブジェクトの見た目やマークを一気に、ズレなく同期して切り替えます。
 *
 * 2. パワーの溜まり具合（チャージ量）を滑らかに見た目へ反映
 * - 毎フレーム、現在のパワーの溜まり具合（0.0 〜 1.0）を計算し、エフェクトの強さへと変換してマテリアルに送り続けています。
 * - これにより、パワーが溜まるにつれてトロッコが激しく発光するような、ゲームの気持ちよさを引き出すダイナミックなビジュアルの変化を生み出しています。
 *
 * 3. データの「空っぽエラー（Null）」を徹底的にガード
 * - リストの中に、設定し忘れた空っぽのデータ（マテリアルや名前の未指定）が混ざっていても、プログラムが途中でエラーを吐いてフリーズしないよう、すべての処理の前に「中身がちゃんと存在するか」を確認するガード機能を徹底しています。
 *
 * 4. 外部からの不正な数値を弾く、安全な数値丸め（Mathf.Clamp01）
 * - 外部のプログラムから「チャージ量をセットする」という命令を受け取る際、バグなどで「マイナス50」や「プラス100」といったあり得ない異常値が送られてきても、`Mathf.Clamp01` という便利な機能を使って、自動で安全な範囲（0.0 〜 1.0の間）にピタッと収めます。これにより、見た目のエフェクトがバグって画面が真っ白になるような事故を未然に防いでいます。
 */

using System;
using System.Collections.Generic;
using UnityEngine;

public class MaterialManager : MonoBehaviour
{
    [Serializable]
    public class TargetMaterial
    {
        [Tooltip("制御対象のマテリアル")]
        public Material Material;

        [Tooltip("じゃんけんのタイプ（グー:1, チョキ:2, パー:3）などを渡すInt/Floatプロパティ名")]
        public string IntegerPropertyName;

        [Tooltip("チャージ量などの値を渡すFloatプロパティ名")]
        public string FloatPropertyName;

        [Tooltip("エフェクトの基準輝度・強度など")]
        public float Intensity = 2f;
    }

    [Header("じゃんけん属性変更マテリアルリスト")]
    [SerializeField] private List<TargetMaterial> _jankenMaterialList = new List<TargetMaterial>();

    [Header("チャージ量変動マテリアルリスト")]
    [SerializeField] private List<TargetMaterial> _floatChangeMaterialList = new List<TargetMaterial>();

    [Header("チャージ設定")]
    [Tooltip("チャージ量によって変化するマテリアルプロパティの最大乗数")]
    [SerializeField] private float _chargeMaxAmount = 1.5f;

    [Tooltip("リアルタイムで変動する現在のチャージ量 (0.0 ～ 1.0)")]
    [Range(0f, 1f)]
    [SerializeField] private float _chargeAmount = 0f;

    private void Update()
    {
        // 毎フレーム現在のチャージ量をマテリアルに反映
        UpdateJankenCharge(_chargeAmount);
    }

    /// <summary>
    /// じゃんけんのタイプに応じてトロッコの色やマークを一括変更する
    /// </summary>
    /// <param name="type">1:グー, 2:チョキ, 3:パー</param>
    public void SetJankenColor(int type)
    {
        foreach (var target in _jankenMaterialList)
        {
            if (target.Material != null && !string.IsNullOrEmpty(target.IntegerPropertyName))
            {
                // シェーダー側の型に合わせてSetIntまたはSetFloatを適用
                target.Material.SetFloat(target.IntegerPropertyName, type);
            }
        }
    }

    /// <summary>
    /// 現在のチャージ状況（0.0〜1.0）に基づき、マテリアルの値を更新する
    /// </summary>
    private void UpdateJankenCharge(float amount)
    {
        float calculatedValue = amount * _chargeMaxAmount;

        foreach (var target in _floatChangeMaterialList)
        {
            // ★修正: IntegerPropertyName から FloatPropertyName に変更
            if (target.Material != null && !string.IsNullOrEmpty(target.FloatPropertyName))
            {
                target.Material.SetFloat(target.FloatPropertyName, calculatedValue);
            }
        }
    }

    /// <summary>
    /// 外部（GameManagerやプレイヤーのスクリプトなど）からチャージ量を動的に設定するAPI
    /// </summary>
    /// <param name="amount">チャージ量 (0.0 ～ 1.0)</param>
    public void SetChargeAmount(float amount)
    {
        _chargeAmount = Mathf.Clamp01(amount); // 0～1の範囲に安全に丸める
    }
}