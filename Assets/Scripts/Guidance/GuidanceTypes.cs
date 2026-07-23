using System;
using UnityEngine;

/// <summary>
/// 冻结 T2 正式实验跨端使用的任务、Guidance、候选与风险策略版本。
/// </summary>
public static class GuidanceProtocolVersions
{
    /// <summary>T2 固定任务定义版本。</summary>
    public const string TaskDefinition = "t2-main-target-v2";
    /// <summary>Unity 与 Backend 精确 JSON 契约版本。</summary>
    public const string GuidanceSchema = "guidance-schema-v2";
    /// <summary>T2 Candidate Set 生成策略版本。</summary>
    public const string CandidatePolicy = "t2-candidate-policy-v1";
    /// <summary>Unity 权威运行时风险策略版本。</summary>
    public const string RiskPolicy = "unity-risk-policy-v1";
}

/// <summary>序列化 Physical Boundary 顶点的协议对象。</summary>
[Serializable]
public sealed class GuidanceRiskBoundaryContext
{
    /// <summary>Physical XZ 平面的边界顶点数组，单位：米。</summary>
    public float[][] points = Array.Empty<float[]>();
}

/// <summary>序列化单个 Physical Obstacle 的协议对象。</summary>
[Serializable]
public sealed class GuidanceRiskObstacleContext
{
    /// <summary>障碍物 ID。</summary>
    public string id;
    /// <summary>Physical XZ 平面的圆心，单位：米。</summary>
    public float[] center = Array.Empty<float>();
    /// <summary>障碍物半径，单位：米。</summary>
    public float radius;
}

/// <summary>
/// GuidanceRequest 携带的 Unity 权威风险快照；由内部只读状态深拷贝生成。
/// </summary>
[Serializable]
public sealed class GuidanceRiskSnapshot
{
    /// <summary>风险事实生产者，正式运行固定为 unity。</summary>
    public string source;
    /// <summary>本次采样的唯一快照 ID。</summary>
    public string risk_snapshot_id;
    /// <summary>执行相关风险发生实质变化时才递增的修订号。</summary>
    public int risk_revision;
    /// <summary>生成快照时使用的 Calibration ID。</summary>
    public string calibration_id;
    /// <summary>ISO 8601 UTC 采样时间。</summary>
    public string sampled_at_utc;
    /// <summary>生成快照的风险策略版本。</summary>
    public string risk_policy_version;
    /// <summary>当前 Physical Space ID。</summary>
    public string physical_space;
    /// <summary>用户在 Physical XZ 平面中的位置，单位：米。</summary>
    public float[] physical_position = Array.Empty<float>();
    /// <summary>用户在 Physical XZ 平面中的单位前向方向。</summary>
    public float[] physical_forward = Array.Empty<float>();
    /// <summary>边界原始上下文。</summary>
    public GuidanceRiskBoundaryContext boundary_context = new GuidanceRiskBoundaryContext();
    /// <summary>障碍物原始上下文。</summary>
    public GuidanceRiskObstacleContext[] obstacle_context = Array.Empty<GuidanceRiskObstacleContext>();
    /// <summary>前方 clearance，单位：米。</summary>
    public float clearance_forward;
    /// <summary>左侧 clearance，单位：米。</summary>
    public float clearance_left;
    /// <summary>右侧 clearance，单位：米。</summary>
    public float clearance_right;
    /// <summary>统一风险等级。</summary>
    public string risk_level;
    /// <summary>安全方向。</summary>
    public string[] safe_directions = Array.Empty<string>();
    /// <summary>不安全方向。</summary>
    public string[] unsafe_directions = Array.Empty<string>();
    /// <summary>是否达到 Critical 风险。</summary>
    public bool critical;
    /// <summary>是否需要确定性 Reset。</summary>
    public bool reset_required;

    /// <summary>从 Unity 内部只读快照创建独立协议副本。</summary>
    public static GuidanceRiskSnapshot From(PhysicalRiskState state)
    {
        if (state == null)
        {
            return null;
        }

        var boundary = new float[state.boundary_context.Count][];
        for (var i = 0; i < state.boundary_context.Count; i++)
        {
            boundary[i] = GuidanceTypeUtility.Vector2ToArray(state.boundary_context[i]);
        }

        var obstacles = new GuidanceRiskObstacleContext[state.obstacle_context.Count];
        for (var i = 0; i < state.obstacle_context.Count; i++)
        {
            var obstacle = state.obstacle_context[i];
            obstacles[i] = new GuidanceRiskObstacleContext
            {
                id = obstacle.id,
                center = GuidanceTypeUtility.Vector2ToArray(obstacle.center),
                radius = obstacle.radius
            };
        }

        return new GuidanceRiskSnapshot
        {
            source = state.source,
            risk_snapshot_id = state.risk_snapshot_id,
            risk_revision = state.risk_revision,
            calibration_id = state.calibration_id,
            sampled_at_utc = state.sampled_at_utc,
            risk_policy_version = state.risk_policy_version,
            physical_space = state.physical_space,
            physical_position = GuidanceTypeUtility.Vector2ToArray(state.user_physical_position),
            physical_forward = GuidanceTypeUtility.Vector2ToArray(state.user_physical_forward),
            boundary_context = new GuidanceRiskBoundaryContext { points = boundary },
            obstacle_context = obstacles,
            clearance_forward = state.clearance_forward,
            clearance_left = state.clearance_left,
            clearance_right = state.clearance_right,
            risk_level = state.boundary_risk,
            safe_directions = CopyStrings(state.safe_directions),
            unsafe_directions = CopyStrings(state.unsafe_directions),
            critical = state.critical,
            reset_required = state.reset_required
        };
    }

    private static string[] CopyStrings(System.Collections.Generic.IReadOnlyList<string> values)
    {
        var result = new string[values != null ? values.Count : 0];
        for (var i = 0; i < result.Length; i++) result[i] = values[i];
        return result;
    }
}

/// <summary>
/// Unity 发送给后端 /api/guidance 的请求数据，字段名必须与后端 schema 保持一致。
/// </summary>
[Serializable]
public sealed class GuidanceRequest
{
    /// <summary>当前 T2 任务定义版本。</summary>
    public string task_definition_version = GuidanceProtocolVersions.TaskDefinition;

    /// <summary>当前 Guidance JSON Schema 版本。</summary>
    public string guidance_schema_version = GuidanceProtocolVersions.GuidanceSchema;

    /// <summary>当前 Candidate Set 生成策略版本。</summary>
    public string candidate_policy_version = GuidanceProtocolVersions.CandidatePolicy;

    /// <summary>当前 Unity 风险策略版本。</summary>
    public string risk_policy_version = GuidanceProtocolVersions.RiskPolicy;

    /// <summary>本次实验会话 ID。</summary>
    public string session_id;

    /// <summary>请求使用的 Physical Space Calibration ID。</summary>
    public string calibration_id;

    /// <summary>单次 HTTP 请求的唯一 ID；重试必须生成新值。</summary>
    public string request_id;

    /// <summary>本次规划决策的唯一 ID；同一决策内的重试保持不变。</summary>
    public string decision_id;

    /// <summary>请求生成时的任务状态版本。</summary>
    public int state_version;

    /// <summary>请求决策使用的 deterministic risk 快照 ID。</summary>
    public string risk_snapshot_id;

    /// <summary>当前 Guidance Target 的协议角色。</summary>
    public string target_role = "route_subgoal";

    /// <summary>
    /// 当前实验条件，例如 ours_full 或 baseline 条件名。
    /// </summary>
    public string condition = "ours_full";

    /// <summary>
    /// 当前任务 ID，用于后端区分任务阶段和目标。
    /// </summary>
    public string task_id = "T2_main_target";

    /// <summary>
    /// 当前 physical tracking space 的配置 ID。
    /// </summary>
    public string physical_space_id = "wide_room";

    /// <summary>
    /// 用户当前所在或正在观看的展品 ID。
    /// </summary>
    public string current_exhibit = "E1";

    /// <summary>Unity 生成的完整权威 Physical Risk 快照。</summary>
    public GuidanceRiskSnapshot risk_snapshot;

    /// <summary>兼容旧诊断工具的物理位置镜像；必须与 risk_snapshot 一致，单位：米。</summary>
    public float[] user_physical_position = Array.Empty<float>();

    /// <summary>兼容旧诊断工具的物理朝向镜像；必须与 risk_snapshot 一致。</summary>
    public float[] user_physical_forward = Array.Empty<float>();

    /// <summary>
    /// 当前参与者 ID，用于实验日志和后端追踪。
    /// </summary>
    public string participant_id = "P001";

    /// <summary>
    /// 可选自然语言任务描述，供后端生成更贴合上下文的 guidance。
    /// </summary>
    public string user_task;

    /// <summary>
    /// 语义上的下一目标展品 ID。
    /// </summary>
    public string semantic_next_target = "E5";

    /// <summary>
    /// 用户在虚拟展厅中的世界位置数组，单位：米。
    /// </summary>
    public float[] user_virtual_position = Array.Empty<float>();

    /// <summary>
    /// 用户在虚拟展厅中的世界朝向数组。
    /// </summary>
    public float[] user_virtual_forward = Array.Empty<float>();
}

/// <summary>
/// 后端 verifier 的最终安全检查结果；approved=false 时 Unity 不能执行导览表现。
/// </summary>
[Serializable]
public sealed class VerifierResult
{
    /// <summary>
    /// 后端最终是否允许 Unity 执行本次 guidance。
    /// </summary>
    public bool approved;

    /// <summary>
    /// 拒绝或警告原因，approved=false 时用于 UI 和调试日志。
    /// </summary>
    public string reason;
}

/// <summary>
/// 后端返回给 Unity 的统一 guidance JSON，字段名不要随意修改。
/// </summary>
[Serializable]
public sealed class GuidanceResponse
{
    /// <summary>对应请求的 T2 任务定义版本。</summary>
    public string task_definition_version;

    /// <summary>对应请求的 Guidance JSON Schema 版本。</summary>
    public string guidance_schema_version;

    /// <summary>对应请求的 Candidate Set 生成策略版本。</summary>
    public string candidate_policy_version;

    /// <summary>对应请求的 Unity 风险策略版本。</summary>
    public string risk_policy_version;

    /// <summary>对应 GuidanceRequest 的会话 ID。</summary>
    public string session_id;

    /// <summary>对应 GuidanceRequest 的校准 ID。</summary>
    public string calibration_id;

    /// <summary>对应 GuidanceRequest 的请求 ID。</summary>
    public string request_id;

    /// <summary>对应 GuidanceRequest 的决策 ID。</summary>
    public string decision_id;

    /// <summary>对应 GuidanceRequest 的任务状态版本。</summary>
    public int state_version;

    /// <summary>对应 GuidanceRequest 的风险快照 ID。</summary>
    public string risk_snapshot_id;

    /// <summary>对应风险快照的执行相关修订号。</summary>
    public int risk_revision;

    /// <summary>后端确认的 Guidance Target 协议角色。</summary>
    public string target_role;

    /// <summary>
    /// 后端选择的目标展品 ID。
    /// </summary>
    public string selected_target;

    /// <summary>
    /// 路线决策标签，用于调试和后续日志分析。
    /// </summary>
    public string route_decision;

    /// <summary>
    /// 导览员需要展示给用户的自然语言话术。
    /// </summary>
    public string narration;

    /// <summary>
    /// 弱视线提示文本或方向标签。
    /// </summary>
    public string gaze_cue;

    /// <summary>
    /// 视线提示应关注的目标展品或 Anchor ID。
    /// </summary>
    public string gaze_target_id;

    /// <summary>
    /// 导览员 avatar 应执行的动作标签。
    /// </summary>
    public string avatar_action;

    /// <summary>
    /// 给用户的身体移动指令，例如 pause、turn_left 或 continue。
    /// </summary>
    public string movement_instruction;

    /// <summary>
    /// 后端推断的 physical 行动意图，供 verifier 和调试面板核对。
    /// </summary>
    public string physical_intent;

    /// <summary>
    /// 后端 verifier 的最终安全结果。
    /// </summary>
    public VerifierResult verifier;
}

/// <summary>
/// Guidance 系统默认请求上下文，后续 ExperimentDashboard 可用正式实验编排覆盖。
/// </summary>
[Serializable]
public sealed class GuidanceRequestContext
{
    /// <summary>当前实验会话 ID；为空时 AgentClient 从 ExperimentLogger 获取。</summary>
    public string session_id;

    /// <summary>当前 Physical Space Calibration ID。</summary>
    public string calibration_id;

    /// <summary>协调器分配的决策 ID；为空时 AgentClient 会生成独立决策 ID。</summary>
    public string decision_id;

    /// <summary>请求对应的任务状态版本。</summary>
    public int state_version;

    /// <summary>当前规划决策使用的 Physical Risk 快照 ID。</summary>
    public string risk_snapshot_id;

    /// <summary>协调器选择的 Guidance Target 协议角色。</summary>
    public string target_role = "route_subgoal";

    /// <summary>
    /// 默认实验条件，例如 B1_mllm_only 或 ours_full。
    /// </summary>
    [Tooltip("实验条件，例如 B1_mllm_only 或 ours_full。")]
    public string condition = "ours_full";

    /// <summary>
    /// 默认任务 ID，例如 T2_main_target。
    /// </summary>
    [Tooltip("任务 ID，例如 T2_main_target。")]
    public string task_id = "T2_main_target";

    /// <summary>
    /// 默认当前展品 ID。
    /// </summary>
    [Tooltip("当前展品 ID，例如 E1。")]
    public string current_exhibit = "E1";

    /// <summary>
    /// 默认语义下一目标展品 ID。
    /// </summary>
    [Tooltip("语义上的下一目标，例如核心 demo 中的 E5。")]
    public string semantic_next_target = "E5";

    /// <summary>
    /// 默认参与者 ID，正式实验可由 session 管理器覆盖。
    /// </summary>
    [Tooltip("参与者 ID，Phase 5 会由 session 管理器接管。")]
    public string participant_id = "P001";

    /// <summary>
    /// 默认自然语言任务描述，MVP 阶段可以为空。
    /// </summary>
    [Tooltip("可选自然语言任务描述，MVP 可为空。")]
    public string user_task;
}

/// <summary>
/// Guidance DTO 的辅助方法，集中处理 Unity 向后端发送的数组格式。
/// </summary>
public static class GuidanceTypeUtility
{
    /// <summary>将 Unity TargetRole 转换为跨端稳定协议值。</summary>
    public static string TargetRoleToProtocol(NavigationTargetRole role)
    {
        switch (role)
        {
            case NavigationTargetRole.FinalTarget:
                return "final_target";
            case NavigationTargetRole.TurnCueAnchor:
                return "turn_cue_anchor";
            case NavigationTargetRole.NavigableDetour:
                return "navigable_detour";
            default:
                return "route_subgoal";
        }
    }

    /// <summary>
    /// 将二维向量转换为后端 JSON 使用的 float 数组。
    /// </summary>
    public static float[] Vector2ToArray(Vector2 value)
    {
        return new[] { value.x, value.y };
    }

    /// <summary>
    /// 将三维向量转换为后端 JSON 使用的 float 数组。
    /// </summary>
    public static float[] Vector3ToArray(Vector3 value)
    {
        return new[] { value.x, value.y, value.z };
    }

    /// <summary>
    /// 将 verifier approved 状态转换为调试面板显示文本。
    /// </summary>
    public static string ApprovedText(GuidanceResponse response)
    {
        if (response == null || response.verifier == null)
        {
            return "false";
        }

        return response.verifier.approved ? "true" : "false";
    }
}
