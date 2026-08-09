using UnityEngine;

/// <summary>统一 RDW 层在 Unity XZ 平面使用的 yaw 约定。</summary>
public static class RdwYawConvention
{
    /// <summary>以 +Z 为零度，向 Unity +X 方向的右转为正，单位：度。</summary>
    public static float ForwardToYaw(Vector2 forward)
    {
        if (forward.sqrMagnitude <= 0.000001f) return 0f;
        var normalized = forward.normalized;
        return NormalizeYaw(Mathf.Atan2(normalized.x, normalized.y) * Mathf.Rad2Deg);
    }

    /// <summary>将统一 yaw 转换为 XZ 平面单位前向。</summary>
    public static Vector2 YawToForward(float yawDegrees)
    {
        var radians = NormalizeYaw(yawDegrees) * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
    }

    /// <summary>计算从旧 yaw 到新 yaw 的最短有符号增量，右转为正，单位：度。</summary>
    public static float SignedDeltaYaw(float previousYawDegrees, float currentYawDegrees)
    {
        return Mathf.DeltaAngle(previousYawDegrees, currentYawDegrees);
    }

    /// <summary>计算两个 XZ 前向之间的最短有符号 yaw，右转为正，单位：度。</summary>
    public static float SignedDeltaYaw(Vector2 previousForward, Vector2 currentForward)
    {
        return SignedDeltaYaw(ForwardToYaw(previousForward), ForwardToYaw(currentForward));
    }

    /// <summary>将旧 MotionDetector 的左转为正增量转换为 RDW 右转为正。</summary>
    public static float FromLegacyMotionDelta(float legacySignedYawDeltaDegrees)
    {
        return -legacySignedYawDeltaDegrees;
    }

    /// <summary>规范化到 [-180, 180]，并拒绝非有限输入。</summary>
    public static float NormalizeYaw(float yawDegrees)
    {
        return IsFinite(yawDegrees) ? Mathf.DeltaAngle(0f, yawDegrees) : 0f;
    }

    /// <summary>判断浮点值是否可安全进入 RDW 计算。</summary>
    public static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>判断二维向量是否为有限且非零值。</summary>
    public static bool IsFinite(Vector2 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && value.sqrMagnitude > 0.000001f;
    }
}
