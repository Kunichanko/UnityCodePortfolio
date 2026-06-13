/**
 * @file MaterialColorController.cs
 * @brief ライブステージのペンライト発光と、色合わせインタラクティブギミックを制御する高効率マテリアルコントローラー
 *
 * 【このスクリプトを作った目的】
 * ライブ演出の核となるペンライトの「カラーサイクリング（振る動作と連動）」や、「ツマミを使った色合わせパズル」といった、
 * 視覚的かつインタラクティブなギミックを、画面の描画パフォーマンスを一切落とさずに実現するために作成しました。
 * 外部の振幅検知スクリプト (`ShakeDetector.cs`) などから呼び出され、入力に応じたリアルタイムなビジュアルフィードバックを返します。
 *
 * 【主要な機能と、そのための工夫】
 *
 * 1. MaterialPropertyBlock による「ドローコールの最適化とメモリリーク防止」
 * - 大量、あるいは頻繁にオブジェクトの色を変更する際、通常のマテリアル操作（`renderer.material.color = ...`）を行うと、内部でマテリアルがインスタンス化（複製）され、メモリリークやドローコール（描画負荷）の増大を招きます。
 * - 本スクリプトでは `MaterialPropertyBlock` を採用することで、単一のマテリアルアセットを共有したまま、GPU側で各オブジェクトの色情報のみを効率的に上書きし、超軽量な描画処理を実現しています。
 *
 * 2. Shader.PropertyToID による「ランタイム処理の高速化」
 * - 毎フレームのように実行される色の更新処理において、文字列（`"_BaseColor"` など）でシェーダープロパティを検索すると文字比較のコストが発生します。
 * - 初期化フェーズ (`Awake`) で予めプロパティ名を整数型のID (`propertyId`) に変換してキャッシュしておくことで、CPUの計算負荷を最小限に抑え、爆速で処理が回るようにしています。
 *
 * 3. インタラクティブな色合わせゲームロジック（ツマミギミック）
 * - ランダムに生成されたお題の色 (`_tumamiSampleColor`) に対し、プレイヤーがツマミを操作してRGBの各値を近づけていくギミックを搭載しています。
 * - 判定処理 (`TumamiColorCheck`) には、Unity.Mathematics の高速な絶対値計算 (`math.abs`) を用い、設定された許容誤差 (`colorDifference`) 以内に入ったかどうかを毎フレーム厳密に判定します。
 *
 * 4. 演出と進行管理の疎結合化（UnityEvent の活用）
 * - 色の変更時やクリア時、ギミック成功時には `UnityEvent` やパーティクルシステム (`ParticleSystem`) を直接発火させています。
 * - これにより、プログラムのコアロジック（色管理）と、派手なビジュアル演出（エフェクトや音、他システムへの通知）が綺麗に分離（疎結合化）され、演出の追加や調整が容易な設計になっています。
 */


using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Events;

public class MaterialColorController : MonoBehaviour
{
    [System.Serializable]
    public class TargetMaterial
    {
        public Renderer targetRenderer;     // Materialを直接持つのではなくRendererを持つのがPropertyBlockの定石
        public string colorPropertyName = "_BaseColor";
        public float intensity = 2.0f;

        [HideInInspector] public int propertyId; // 文字列検索を高速化するためのID
    }

    [SerializeField] private List<Color> colorList = new List<Color>();
    [SerializeField] private List<Color> cycleColorList = new List<Color>();

    [Header("対象マテリアルリスト")]
    public List<TargetMaterial> targets = new List<TargetMaterial>();
    public List<TargetMaterial> cycleTargets = new List<TargetMaterial>();

    [Header("エフェクト・イベント関連")]
    public UnityEvent<Color> onColorChanged;
    public UnityEvent clearEvent;
    public UnityEvent tumamiSuccessEvent;
    [SerializeField] private ParticleSystem celebrateParticle;
    [SerializeField] private ParticleSystem tumamiSuccessParticle;

    [Header("デバッグ・サンプル設定")]
    public Material sampleMaterial = null;
    public float sampleIntensity = 5.0f;
    public int sampleCycle = 0;
    public float samplingTime = 3.0f;
    public int matchCount = 3;

    [Header("ツマミギミック設定")]
    public Material tumamiColorMat = null;
    public float colorDifference = 0.1f;
    public float tumamiIntense = 1.5f;
    public EquippableTool equip;

    // プライベート変数（命名規則を _ で統一）
    private int _cycleNum = 0;
    private int _initialCycle = 0;
    private float _sampleProcessTime = 0f;
    private int _currentMatchCount = 0;
    private bool _isCleared = false;
    private bool _tumamiGimicStarted = false;
    private bool _tumamiSuccess = false;
    private bool _isEquipped = false;

    private float[] _tumamiColor = new float[3];
    private float[] _tumamiSampleColor = new float[3];
    private Color _tumamiChangeColor;
    private MaterialPropertyBlock _propertyBlock; // メモリリークを防ぎ、描画を高速化する機構

    private void Awake()
    {
        _propertyBlock = new MaterialPropertyBlock();
        // 文字列のプロパティ名を事前にID化して実行速度を爆速にする
        foreach (var t in targets) t.propertyId = Shader.PropertyToID(t.colorPropertyName);
        foreach (var t in cycleTargets) t.propertyId = Shader.PropertyToID(t.colorPropertyName);
    }

    private void Start()
    {
        ColorCycling(_initialCycle);
        RandomizeSample();
        UpdateSampleMaterial();

        if (tumamiColorMat != null)
        {
            tumamiColorMat.EnableKeyword("_EMISSION");
        }
        TumamiColorChange(0, 0f);

        if (equip == null) _isEquipped = true;
    }

    private void Update()
    {
        if (equip != null) _isEquipped = equip.isEquipped;

        HandleColorMatchingLogic();
    }

    private void HandleColorMatchingLogic()
    {
        if (_currentMatchCount >= matchCount)
        {
            if (!_isCleared)
            {
                _isCleared = true;
                clearEvent?.Invoke();
            }
            return;
        }

        if (sampleCycle == _cycleNum)
        {
            if (_sampleProcessTime < samplingTime)
            {
                _sampleProcessTime += Time.deltaTime;
            }
            else
            {
                celebrateParticle?.Play();
                RandomizeSample();
                _currentMatchCount++;

                if (_currentMatchCount != matchCount)
                {
                    UpdateSampleMaterial();
                }
                _sampleProcessTime = 0f;
            }
        }
        else
        {
            _sampleProcessTime = 0f;
        }
    }

    public void TumamiColorChange(int num, float value)
    {
        _tumamiColor[num] = value;
        _tumamiChangeColor = new Color(_tumamiColor[0], _tumamiColor[1], _tumamiColor[2], 1f);

        if (!_tumamiSuccess && tumamiColorMat != null)
        {
            tumamiColorMat.SetColor("_EmissionColor", _tumamiChangeColor);
            tumamiColorMat.SetColor("_BaseColor", _tumamiChangeColor * tumamiIntense);

            if (TumamiColorCheck())
            {
                _tumamiSuccess = true;
                tumamiSuccessEvent?.Invoke();
                tumamiSuccessParticle?.Play();
            }
        }
    }

    public void BootTumamiGimic()
    {
        if (_tumamiGimicStarted) return;

        for (int i = 0; i < 3; i++)
        {
            _tumamiSampleColor[i] = Mathf.Pow(UnityEngine.Random.value, UnityEngine.Random.Range(0f, 4f));
        }

        Color color = new Color(_tumamiSampleColor[0], _tumamiSampleColor[1], _tumamiSampleColor[2], 1f);

        // MaterialPropertyBlock を使ってメモリに優しく色を書き換える
        ApplyColorToTargets(targets, color);

        if (sampleMaterial != null)
        {
            sampleMaterial.EnableKeyword("_EMISSION");
            sampleMaterial.SetColor("_EmissionColor", color * sampleIntensity);
            sampleMaterial.SetColor("_BaseColor", color);
        }

        _tumamiGimicStarted = true;
    }

    public bool TumamiColorCheck()
    {
        for (int i = 0; i < 3; i++)
        {
            if (math.abs(_tumamiColor[i] - _tumamiSampleColor[i]) > colorDifference)
            {
                return false;
            }
        }
        return true;
    }

    private void UpdateSampleMaterial()
    {
        if (sampleMaterial != null && cycleColorList.Count > sampleCycle)
        {
            sampleMaterial.EnableKeyword("_EMISSION");
            sampleMaterial.SetColor("_EmissionColor", cycleColorList[sampleCycle] * sampleIntensity);
            sampleMaterial.SetColor("_BaseColor", cycleColorList[sampleCycle]);
        }
    }

    public void RandomizeSample()
    {
        if (cycleColorList.Count <= 1) return;

        int previousCycle = sampleCycle;
        while (sampleCycle == previousCycle)
        {
            sampleCycle = UnityEngine.Random.Range(0, cycleColorList.Count);
        }
    }

    public void ColorCycling(int num)
    {
        if (equip != null && !_isEquipped) return;

        _cycleNum += num;
        if (cycleColorList.Count == 0) return;

        if (_cycleNum >= cycleColorList.Count) _cycleNum %= cycleColorList.Count;
        if (_cycleNum < 0) _cycleNum = cycleColorList.Count + (_cycleNum % cycleColorList.Count);

        SetCycleColor(_cycleNum);
    }

    private void SetCycleColor(int num)
    {
        if (_isCleared || cycleColorList.Count <= num) return;
        ApplyColorToTargets(targets, cycleColorList[num]);
    }

    private void ApplyColorToTargets(List<TargetMaterial> targetList, Color color)
    {
        foreach (var t in targetList)
        {
            if (t.targetRenderer == null) continue;

            // RendererからPropertyBlockを取得して上書き
            t.targetRenderer.GetPropertyBlock(_propertyBlock);

            if (t.colorPropertyName == "_EmissionColor")
            {
                _propertyBlock.SetColor(t.propertyId, color * t.intensity);
            }
            else
            {
                _propertyBlock.SetColor(t.propertyId, color);
            }

            t.targetRenderer.SetPropertyBlock(_propertyBlock);
        }
    }
}