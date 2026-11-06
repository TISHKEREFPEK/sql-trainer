namespace Classroom.Storage;

public sealed class ElectronicsTaskEntity
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? StarterJson { get; set; }
    public bool Restricted { get; set; }
    public int Revision { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}
public sealed class ElectronicsTaskRevisionEntity
{
    public string TaskId { get; set; } = "";
    public int Revision { get; set; }
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? StarterJson { get; set; }
    public bool Restricted { get; set; }
    public long At { get; set; }
}
public sealed class LessonElectronicsTaskEntity
{
    public string LessonId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? StarterJson { get; set; }
    public bool Restricted { get; set; }
    public int Revision { get; set; }
    public int Position { get; set; }
    public long IssuedAt { get; set; }
    public bool Withdrawn { get; set; }
}
public sealed class ElectronicsAssignmentEntity
{
    public string Id { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string InitialProjectJson { get; set; } = "";
    public long OpenedAt { get; set; }
    public string State { get; set; } = "draft";
    public bool CanSubmit { get; set; } = true;
    public int StateVersion { get; set; } = 1;
}
