using System;

[Serializable]
/// <summary>
/// 描述 guidance 可使用的 RDW 干预条件，由风险与策略层生成并作为跨模块数据传递。
/// 字段值使用后端协议约定的英文标签。
/// </summary>
public sealed class RDWAffordance
{
    /// <summary>建议采用的 RDW 干预类型。</summary>
    public string interventionType;
    /// <summary>物理空间中优先使用的方向标签。</summary>
    public string preferredDirection;
    /// <summary>可承载重定向意图的语义展品或 Anchor ID。</summary>
    public string semanticAnchor;
    /// <summary>干预期望达到的物理空间目标。</summary>
    public string physicalGoal;
    /// <summary>必须避免引导用户进入的方向标签。</summary>
    public string avoidedDirection;
}
