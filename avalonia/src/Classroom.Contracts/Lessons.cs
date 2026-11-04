namespace Classroom.Contracts;

public record LessonPolicyRequest(string? GroupId, bool AllowSql = true, bool AllowElectronics = true,
    bool AdmissionOpen = true, bool OpenToAllGroups = false, bool RegistrationOpen = false, bool FreePractice = false);
public record CreateLessonRequest(string Id, LessonPolicyRequest Policy);
public record LessonView(string Id, string? GroupId, string GroupName, string State, long CreatedAt,
    long? StartedAt, long? PausedAt, long? EndedAt, bool AllowSql, bool AllowElectronics,
    bool AdmissionOpen, bool OpenToAllGroups, bool RegistrationOpen, bool FreePractice, bool Activated, int Participants);
public record LessonDashboard(LessonView? Current, LessonView[] History, bool HasMore, int NextOffset);
public record AdmissionDto(string? LessonId, string State, string Message, bool AdmissionOpen,
    bool RegistrationOpen, GroupDto? RegistrationGroup, GroupDto[] Groups);
public record StudentChoice(string Id, string Name);
public record StudentLoginRequest(string GroupId, string StudentId, string Password);
public record StudentRegistrationRequest(string Id, string FullName, string Password);
public record StudentNameRequest(string FullName);
public record StudentLessonDto(LessonView? Lesson, bool CanWork, string Message);
