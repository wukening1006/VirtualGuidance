using System;

[Serializable]
public sealed class ExperimentSession
{
    public string sessionId;
    public string participantId;
    public string condition;
    public string taskId;
    public string physicalSpaceId;
    public string startedAtUtc;

    public bool IsActive => !string.IsNullOrWhiteSpace(sessionId);

    public static ExperimentSession Start(ExperimentConditionSpec spec)
    {
        var timestamp = DateTime.UtcNow;
        return new ExperimentSession
        {
            sessionId = $"{Safe(spec.participantId)}_{Safe(spec.condition)}_{Safe(spec.physicalSpaceId)}_{Safe(spec.taskId)}_{timestamp:yyyyMMdd_HHmmss}",
            participantId = spec.participantId,
            condition = spec.condition,
            taskId = spec.taskId,
            physicalSpaceId = spec.physicalSpaceId,
            startedAtUtc = timestamp.ToString("o")
        };
    }

    private static string Safe(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }
}
