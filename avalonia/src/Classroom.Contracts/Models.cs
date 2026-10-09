using System.Text.Json;

namespace Classroom.Contracts;
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Пустые данные.");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
}

public record ApiError(string Error);
public record StatusDto(bool NeedsSetup, string Version);
public record LoginRequest(string Role, string Login, string Password);
public record LoginDto(string Token, string Role, string Id, ThemePreference Theme, ThemePreset[] ThemePresets);
public record PasswordRequest(string Password);
public record CatalogueItem(string Id, string Title, string Module, string? Project);
public record CatalogueDto(CatalogueItem[] Tasks, string[] Completed, long LockedUntil);
// This type deliberately cannot hold solution, seed, snapshots or unopened hints.
public record StudentTask(string Id, string Title, string Module, string Prompt, string Concept, string Example, string Starter, string[] Terms, Dictionary<string, string> Definitions, string? Project, int VariantIndex, int Revision, bool Restricted, string[] OpenedHints, long[] HintOpenedAt, int HintTotal, string Draft, bool Completed, long LockedUntil);
public record HintRequest(int Index);
public record DraftRequest(string Code);
public record ActivityRequest(int Seconds);
public record ExecuteRequest(string OperationId, string Code, bool Check);
public record SqlResult(string[] Columns, JsonElement[][] Values);
public record ColumnDefinition(string Name, string Type, bool NotNull, bool PrimaryKey);
public record ForeignKeyDefinition(string Column, string Table, string Target);
public record TablePreview(string Name, SqlResult Data, ColumnDefinition[]? Columns = null, ForeignKeyDefinition[]? ForeignKeys = null);
public record ExecutionDto(bool Correct, SqlResult Output, TablePreview[] Tables, string? Error, string? Explanation);
public record ThemePreference(string Preset = "light", Dictionary<string, string>? Colors = null);
public record ThemePreset(string Id, string Name, ThemePreference Theme);
public record ThemePresetRequest(string Name, ThemePreference Theme);
public record GroupDto(string Id, string Name);
public record StudentSummary(string Id, string Login, string? GroupId, int Completed, long LockedUntil);
public record CreateStudentRequest(string Login, string Password, string? GroupId);
public record GroupRequest(string Name);
public record GroupMembershipRequest(string? GroupId);
public record AttemptDto(string Student, string TaskId, string Kind, long At);
public record HintReport(string Student, string TaskId, int VariantIndex, long[] OpenedAt);
public record TaskStatistics(string Student, string TaskId, int ActiveSeconds, int Checks, int Errors, long? CompletedAt);
public record TeacherOverview(StudentSummary[] Students, GroupDto[] Groups, CatalogueItem[] Tasks, AttemptDto[] Attempts, HintReport[] Hints, TaskStatistics[] Statistics);
// Full authoring payloads are sent only on authenticated teacher routes.
public record TeacherTaskDto(string TaskJson, string[] AlternativeJson, bool Restricted, int Revision);
public record TaskRevisionDto(int Revision, long At, bool Deleted, TeacherTaskDto Task);
public record SaveTaskRequest(string TaskJson, bool Restricted);
public record SaveVariantRequest(string TaskJson);
public record ImportPreview(string Id, string SourceHash, int Students, int Assignments, int Projects, int Attempts, int Overrides);
public record ImportCommitRequest(string PreviewId);
public record BackupDto(string Name, long CreatedAt);
public record ServerSettings(string Address = "127.0.0.1", int Port = 47831);
public record ConnectionSettings(string Address, string Fingerprint);
