namespace Classroom.Storage;

public interface ISqlWork
{
    string StudentId { get; set; }
    string TaskId { get; set; }
    string Json { get; set; }
    int Revision { get; set; }
    long? CompletedAt { get; set; }
    string HintsJson { get; set; }
    string Draft { get; set; }
    int ProjectOrder { get; set; }
    int ActiveSeconds { get; set; }
    int Checks { get; set; }
    int Errors { get; set; }
    long? StartedAt { get; set; }
    long? LastActivityAt { get; set; }
    long? LastHeartbeatAt { get; set; }
    string? AcceptedCode { get; set; }
    int? ActiveSecondsAtCompletion { get; set; }
    int TrackingVersion { get; set; }
}

public sealed class LessonSqlWorkEntity : ISqlWork
{
    public string LessonId { get; set; } = "";
    public string PolicyTaskId { get; set; } = "";
    public string? Project { get; set; }
    public bool RequireNewSolution { get; set; }
    public long CreatedAt { get; set; }
    public string? PreviousJson { get; set; }
    public string? PreviousAcceptedCode { get; set; }
    public long? PreviousCompletedAt { get; set; }
    public string StudentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Json { get; set; } = "";
    public int Revision { get; set; } = 1;
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

public sealed class LessonSqlSnapshotEntity
{
    public string LessonId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Project { get; set; } = "";
    public string Base64 { get; set; } = "";
}
