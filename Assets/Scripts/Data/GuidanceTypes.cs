using System;
using UnityEngine;

/// <summary>
/// 中文说明：Unity 发送给后端 /api/guidance 的请求数据，字段名必须与后端 schema 保持一致。
/// </summary>
[Serializable]
public sealed class GuidanceRequest
{
    public string condition = "ours_full";
    public string task_id = "T2_main_target";
    public string physical_space_id = "wide_room";
    public string current_exhibit = "E1";
    public float[] user_physical_position = Array.Empty<float>();
    public float[] user_physical_forward = Array.Empty<float>();
    public string participant_id = "P001";
    public string user_task;
    public string semantic_next_target = "E5";
    public float[] user_virtual_position = Array.Empty<float>();
    public float[] user_virtual_forward = Array.Empty<float>();
}

/// <summary>
/// 中文说明：后端 verifier 的最终安全检查结果；approved=false 时 Unity 不能执行导览表现。
/// </summary>
[Serializable]
public sealed class VerifierResult
{
    public bool approved;
    public string reason;
}

/// <summary>
/// 中文说明：后端返回给 Unity 的统一 guidance JSON，字段名不要随意修改。
/// </summary>
[Serializable]
public sealed class GuidanceResponse
{
    public string selected_target;
    public string route_decision;
    public string narration;
    public string gaze_cue;
    public string gaze_target_id;
    public string avatar_action;
    public string movement_instruction;
    public string physical_intent;
    public VerifierResult verifier;
}

/// <summary>
/// 中文说明：Guidance 系统默认请求上下文，后续 ExperimentDashboard 可用正式实验编排覆盖。
/// </summary>
[Serializable]
public sealed class GuidanceRequestContext
{
    [Tooltip("实验条件，例如 B1_mllm_only 或 ours_full。")]
    public string condition = "ours_full";
    [Tooltip("任务 ID，例如 T2_main_target。")]
    public string task_id = "T2_main_target";
    [Tooltip("当前展品 ID，例如 E1。")]
    public string current_exhibit = "E1";
    [Tooltip("语义上的下一目标，例如核心 demo 中的 E5。")]
    public string semantic_next_target = "E5";
    [Tooltip("参与者 ID，Phase 5 会由 session 管理器接管。")]
    public string participant_id = "P001";
    [Tooltip("可选自然语言任务描述，MVP 可为空。")]
    public string user_task;
}

/// <summary>
/// 中文说明：Guidance DTO 的辅助方法，集中处理 Unity 向后端发送的数组格式。
/// </summary>
public static class GuidanceTypeUtility
{
    public static float[] Vector2ToArray(Vector2 value)
    {
        return new[] { value.x, value.y };
    }

    public static float[] Vector3ToArray(Vector3 value)
    {
        return new[] { value.x, value.y, value.z };
    }

    public static string ApprovedText(GuidanceResponse response)
    {
        if (response == null || response.verifier == null)
        {
            return "false";
        }

        return response.verifier.approved ? "true" : "false";
    }
}
