namespace Classroom.Storage;

public sealed class LessonSessionEntity
{
    public string Id { get; set; } = "";
    public string TeacherId { get; set; } = "teacher";
    public string? GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public string State { get; set; } = "preparing";
    public long CreatedAt { get; set; }
    public long? StartedAt { get; set; }
    public long? PausedAt { get; set; }
    public long? EndedAt { get; set; }
    public bool AllowSql { get; set; } = true;
    public bool AllowElectronics { get; set; }
    public bool AdmissionOpen { get; set; }
    public bool OpenToAllGroups { get; set; }
    public bool RegistrationOpen { get; set; }
    public bool FreePractice { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 || string.IsNullOrWhiteSpace(TeacherId) ||
            string.IsNullOrWhiteSpace(GroupName) || GroupName.Length > 200 || CreatedAt < 1 ||
            State is not ("preparing" or "running" or "paused" or "finished") || (!AllowSql && !AllowElectronics))
            throw new InvalidDataException("Некорректное занятие.");
        if (State == "preparing" && (StartedAt is not null || PausedAt is not null || EndedAt is not null) ||
            State is "running" or "paused" && (StartedAt is null || EndedAt is not null) ||
            State == "finished" && EndedAt is null ||
            StartedAt < CreatedAt || PausedAt < (StartedAt ?? CreatedAt) || EndedAt < (StartedAt ?? CreatedAt) ||
            State == "paused" && PausedAt is null || State == "running" && PausedAt is not null ||
            PausedAt > EndedAt)
            throw new InvalidDataException("Некорректные даты или состояние занятия.");
        if (RegistrationOpen && GroupId is null)
            throw new InvalidDataException("Для регистрации выберите существующую группу.");
    }
}

public sealed class LessonParticipantEntity
{
    public string SessionId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public long JoinedAt { get; set; }
    public long LastSeenAt { get; set; }
    public string GroupNameAtEntry { get; set; } = "";
}
