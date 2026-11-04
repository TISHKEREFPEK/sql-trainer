namespace Classroom.Storage;

// The lesson keeps its issued edition even when the course catalogue is edited later.
public sealed class LessonSqlTaskEntity
{
    public string LessonId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public int Position { get; set; }
    public int Revision { get; set; }
    public string Json { get; set; } = "";
    public string AlternativesJson { get; set; } = "[]";
    public long IssuedAt { get; set; }
    public bool Withdrawn { get; set; }
}
