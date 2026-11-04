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
public record LessonSqlSelectionRequest(string[] TaskIds, string[]? RequireNewTaskIds = null);
public record LessonSqlTaskInfo(string Id, string Title, string Module, string? Project, int Revision, long IssuedAt, bool RequireNewSolution = false);
public record LessonSqlSelection(LessonSqlTaskInfo[] Tasks);
public record StudentLessonWork(string Id, string Title, string Module, string State, int? Variant, int? Revision, long? CompletedAt, bool RequireNewSolution = false, long? PreviousCompletedAt = null);
public record StudentHomeDto(string Name, string Group, LessonView Lesson, StudentLessonWork[] Required);
public record LessonSqlWorkDetail(string LessonId, string StudentId, string TaskId, string Title, string Prompt,
    int Variant, int Revision, bool RequireNewSolution, string Draft, string? AcceptedCode, long? CompletedAt,
    long CreatedAt, long? StartedAt, int Checks, int Errors, int ActiveSeconds, string? PreviousTitle,
    string? PreviousPrompt, string? PreviousAcceptedCode, long? PreviousCompletedAt);
