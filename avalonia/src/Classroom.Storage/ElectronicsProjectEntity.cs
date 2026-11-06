namespace Classroom.Storage;

public sealed class ElectronicsProjectEntity
{
    public string StudentId { get; set; } = "";
    public string Id { get; set; } = "";
    public string CreationHash { get; set; } = "";
    public string InitialProjectJson { get; set; } = "";
    public string ProjectJson { get; set; } = "";
    public string ProjectHash { get; set; } = "";
    public string Name { get; set; } = "";
    public int Parts { get; set; }
    public int Wires { get; set; }
    public int Revision { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public string State { get; set; } = "working";
    public bool CanSubmit { get; set; } = true;
    public int StateVersion { get; set; } = 1;
}

public sealed class ElectronicsPracticeEntity
{
    public string StudentId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string LessonId { get; set; } = "";
    public string ProjectJson { get; set; } = "";
    public string ProjectHash { get; set; } = "";
    public string Name { get; set; } = "";
    public int Parts { get; set; }
    public int Wires { get; set; }
    public int Revision { get; set; }
    public long? OpenedAt { get; set; }
    public long? FirstSavedAt { get; set; }
    public long UpdatedAt { get; set; }
}
