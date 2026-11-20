namespace Classroom.Storage;

// Observation of course work during one lesson; never used as an execution database.
public sealed class LessonSqlPracticeEntity : ISqlWork
{
    public string LessonId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Json { get; set; } = "";
    public int Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public bool Partial { get; set; }
    public long? CompletedAt { get; set; }
    public string HintsJson { get; set; } = "[]";
    public string Draft { get; set; } = "";
    public int ProjectOrder { get; set; }
    public int ActiveSeconds { get; set; }
    public int Checks { get; set; }
    public int Errors { get; set; }
    public long? StartedAt { get; set; }
    public long? LastActivityAt { get; set; }
    public long? LastHeartbeatAt { get; set; }
    public string? AcceptedCode { get; set; }
    public int? ActiveSecondsAtCompletion { get; set; }
    public int TrackingVersion { get; set; } = 1;
}
