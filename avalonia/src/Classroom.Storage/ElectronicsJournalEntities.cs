namespace Classroom.Storage;

public sealed class ElectronicsJournalStreamEntity
{
    public string StudentId { get; set; } = "";
    public string Id { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string? AssignmentId { get; set; }
    public string Title { get; set; } = "";
    public long StartedAt { get; set; }
    public long ReceivedAt { get; set; }
    public string BaseJson { get; set; } = "";
    public string TipJson { get; set; } = "";
    public long Sequence { get; set; }
    public string? ForkedFrom { get; set; }
}
public sealed class ElectronicsJournalChangeEntity
{
    public string StudentId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public long Sequence { get; set; }
    public string EventId { get; set; } = "";
    public string ChangeJson { get; set; } = "";
    public long ReceivedAt { get; set; }
    public string Actor { get; set; } = "student";
    public string? Checkpoint { get; set; }
}
