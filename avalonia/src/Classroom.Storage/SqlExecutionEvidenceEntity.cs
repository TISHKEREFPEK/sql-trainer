namespace Classroom.Storage;

// Deduplicate identical before-states within a pupil, retaining profile deletion.
public sealed class SqlExecutionStateEntity
{
    public string StudentId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Base64 { get; set; } = "";
}
public sealed class SqlExecutionEvidenceEntity
{
    public string AttemptId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string TaskJson { get; set; } = "";
    public int Revision { get; set; }
    public bool Check { get; set; }
    public string? BeforeHash { get; set; }
    public string ResultJson { get; set; } = "";
    public string? OperationId { get; set; }
}
