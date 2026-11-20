namespace Classroom.Storage;

// Snapshot membership is distinct from admission and from a pupil's current group.
public sealed class LessonRosterEntity
{
    public string LessonId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string StudentName { get; set; } = "";
    public string? GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public bool GroupKnown { get; set; } = true;
    public string Source { get; set; } = "selected-group";
    public long AddedAt { get; set; }
}
