using System;

/// <summary>正式实验使用的 Planner 类型。</summary>
public enum ExperimentPlannerMode
{
    /// <summary>只使用静态语义和虚拟空间图。</summary>
    SemanticOnly,
    /// <summary>使用 Unity RDW Top-K 路线后交给 LLM 选择。</summary>
    RdwGroundedTopK
}

/// <summary>正式实验使用的 Reset 策略。</summary>
public enum ExperimentResetMode
{
    /// <summary>只按物理安全朝向恢复。</summary>
    Normal,
    /// <summary>在安全候选中优先保持当前主题路线。</summary>
    SemanticContextualized
}

/// <summary>
/// 固定一次实验条件的跨模块能力开关，避免 Unity 与 Backend 各自推导条件语义。
/// </summary>
[Serializable]
public sealed class ExperimentProfile
{
    /// <summary>正式实验条件 ID。</summary>
    public string conditionId;
    /// <summary>Planner 类型。</summary>
    public ExperimentPlannerMode plannerMode;
    /// <summary>是否启用 Continuous RDW。</summary>
    public bool continuousRdwEnabled;
    /// <summary>RDW 规划模式。</summary>
    public RdwPlanningMode rdwPlanningMode;
    /// <summary>Reset 策略。</summary>
    public ExperimentResetMode resetMode;

    /// <summary>创建固定不可变语义的运行时副本。</summary>
    public ExperimentProfile(
        string conditionId,
        ExperimentPlannerMode plannerMode,
        bool continuousRdwEnabled,
        RdwPlanningMode rdwPlanningMode,
        ExperimentResetMode resetMode)
    {
        this.conditionId = conditionId ?? string.Empty;
        this.plannerMode = plannerMode;
        this.continuousRdwEnabled = continuousRdwEnabled;
        this.rdwPlanningMode = rdwPlanningMode;
        this.resetMode = resetMode;
    }

    /// <summary>返回当前 Profile 的独立副本。</summary>
    public ExperimentProfile Clone()
    {
        return new ExperimentProfile(
            conditionId,
            plannerMode,
            continuousRdwEnabled,
            rdwPlanningMode,
            resetMode);
    }
}
