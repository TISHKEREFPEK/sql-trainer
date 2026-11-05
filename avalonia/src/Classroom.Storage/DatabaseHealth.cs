using System.Text.Json;
using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Storage;

public static class DatabaseHealth
{
    // Validation reads metadata/JSON only. Student SQL is never executed here.
    public static void Validate(ClassroomDb db)
    {
        db.Database.OpenConnection();
        using (var integrity = db.Database.GetDbConnection().CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (integrity.ExecuteScalar() as string != "ok")
                throw new InvalidDataException("Повреждён файл SQLite.");
        }
        using (var foreignKeys = db.Database.GetDbConnection().CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var rows = foreignKeys.ExecuteReader();
            if (rows.Read())
                throw new InvalidDataException("Нарушены связи между записями.");
        }
        var profiles = db.Profiles.AsNoTracking().ToArray();
        var electronicsStreams = db.ElectronicsJournalStreams.AsNoTracking().ToArray();
        foreach (var stream in electronicsStreams)
        {
            if (!profiles.Any(p => p.Id == stream.StudentId && p.Role == "student") || !Guid.TryParseExact(stream.Id, "N", out _) || stream.Sequence is < 0 or > 10000000 || stream.ReceivedAt <= 0 || stream.ReceivedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
                !db.LessonParticipants.Any(p => p.SessionId == stream.LessonId && p.StudentId == stream.StudentId)) throw new InvalidDataException("Некорректный контекст истории электроники.");
            var work = db.ElectronicsWorks.AsNoTracking().SingleOrDefault(w => w.StudentId == stream.StudentId && w.ClientId == stream.ClientId);
            if (work is null || work.AssignmentId != stream.AssignmentId) throw new InvalidDataException("История потеряла исходную копию схемы.");
            if (stream.AssignmentId is { } assigned && !db.ElectronicsAssignments.Any(a => a.Id == assigned && a.StudentId == stream.StudentId && a.LessonId == stream.LessonId)) throw new InvalidDataException("История относится к другому электронному заданию.");
            var seen = new HashSet<string> { stream.Id }; var parent = stream.ForkedFrom;
            while (parent is not null)
            {
                var origin = electronicsStreams.SingleOrDefault(s => s.StudentId == stream.StudentId && s.Id == parent);
                if (origin is null || !seen.Add(parent) || origin.ClientId != stream.ClientId || origin.AssignmentId != stream.AssignmentId || origin.LessonId != stream.LessonId) throw new InvalidDataException("Неверная ветвь истории электроники.");
                parent = origin.ForkedFrom;
            }
            var json = new ElectronicsJournal(1, stream.Id, stream.StartedAt, stream.BaseJson, []).Replay(); long sequence = 0;
            foreach (var step in db.ElectronicsJournalChanges.AsNoTracking().Where(c => c.StudentId == stream.StudentId && c.StreamId == stream.Id).OrderBy(c => c.Sequence))
            {
                var change = Wire.Read<ElectronicsChange>(step.ChangeJson);
                if (step.Sequence != ++sequence || change.Sequence != sequence || step.EventId != change.Id || step.Actor != "student" || step.ReceivedAt <= 0 || step.ReceivedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) throw new InvalidDataException("Нарушена последовательность истории электроники.");
                json = new ElectronicsJournal(1, stream.Id, stream.StartedAt, json, [change with { Sequence = 1 }]).Replay();
                if (step.Checkpoint != (sequence % 64 == 0 ? json : null)) throw new InvalidDataException("Неверная контрольная схема.");
            }
            if (sequence != stream.Sequence || json != stream.TipJson) throw new InvalidDataException("Не совпадает последнее состояние электронной истории.");
        }
        foreach (var stream in db.SqlJournalStreams.AsNoTracking())
        {
            if (!profiles.Any(p => p.Id == stream.StudentId && p.Role == "student") || !Guid.TryParseExact(stream.Id, "N", out _) ||
                stream.Sequence is < 0 or > 10000000 || stream.ReceivedAt <= 0 || stream.StartedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() || stream.WorkScope != "course" && stream.WorkScope != "lesson:" + stream.LessonId)
                throw new InvalidDataException("Некорректный контекст SQL-журнала.");
            if (stream.ForkedFrom is { } parent && (parent == stream.Id || !db.SqlJournalStreams.Any(s => s.StudentId == stream.StudentId && s.Id == parent && s.LessonId == stream.LessonId && s.TaskId == stream.TaskId && s.WorkScope == stream.WorkScope)))
                throw new InvalidDataException("Потеряна исходная ветвь SQL-истории.");
            var code = new SqlTextJournal(1, stream.Id, stream.StartedAt, stream.BaseCode, []).Replay(); long sequence = 0;
            foreach (var step in db.SqlJournalChanges.AsNoTracking().Where(c => c.StudentId == stream.StudentId && c.StreamId == stream.Id).OrderBy(c => c.Sequence))
            {
                var change = Wire.Read<SqlTextChange>(step.ChangeJson);
                if (step.Sequence != ++sequence || change.Sequence != sequence || step.EventId != change.Id || step.Actor != "student" || step.ReceivedAt <= 0 || change.At > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                    throw new InvalidDataException("Нарушена последовательность SQL-истории.");
                code = new SqlTextJournal(1, stream.Id, stream.StartedAt, code, [change with { Sequence = 1 }]).Replay();
                if (step.Checkpoint != (sequence % 64 == 0 ? code : null)) throw new InvalidDataException("Неверная контрольная копия SQL-истории.");
            }
            if (sequence != stream.Sequence || code != stream.TipCode) throw new InvalidDataException("Неверное последнее состояние SQL-истории.");
        }
        if (profiles.Any(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Login) || p.Role is not ("teacher" or "student") || !Passwords.ValidHash(p.PasswordHash)) || profiles.Select(p => p.Login).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Length)
            throw new InvalidDataException("Некорректные профили в резервной копии.");
        foreach (var p in profiles)
        {
            if (p.FullName is not null && (string.IsNullOrWhiteSpace(p.FullName) || p.FullName.Length > 200 || p.FullName.Any(char.IsControl)))
                throw new InvalidDataException("Некорректное ФИО ученика.");
            Themes.Validate(Wire.Read<ThemePreference>(p.ThemeJson));
            Themes.ValidatePresets(Wire.Read<ThemePreset[]>(p.PresetsJson));
        }
        foreach (var row in db.Tasks.AsNoTracking())
            Task(row.Id, row.Json);
        foreach (var row in db.Variants.AsNoTracking())
            Task(row.TaskId, row.Json);
        foreach (var row in db.TaskRevisions.AsNoTracking())
        {
            Task(row.TaskId, row.Json);
            foreach (var alternative in Wire.Read<string[]>(row.AlternativesJson))
                Task(row.TaskId, alternative);
        }
        foreach (var row in db.Assignments.AsNoTracking())
        {
            var task = Task(row.TaskId, row.Json);
            if (Wire.Read<long[]>(row.HintsJson).Length > task.Hints.Length || row.Checks < 0 || row.Errors < 0 || row.Errors > row.Checks || row.ActiveSeconds < 0)
                throw new InvalidDataException("Некорректная статистика в резервной копии.");
        }
        foreach (var row in db.Snapshots.AsNoTracking())
            ElectronImport.ValidateSnapshot(row.Base64);
        foreach (var row in db.Operations.AsNoTracking())
            using (JsonDocument.Parse(row.ResponseJson))
            {
            }
        // Materialize all remaining tables to reject incomplete or incompatible columns.
        _ = db.Groups.AsNoTracking().ToArray();
        foreach (var attempt in db.Attempts.AsNoTracking())
            if (attempt.WorkScope is not null && attempt.WorkScope != "course" && (attempt.LessonId is null || attempt.WorkScope != "lesson:" + attempt.LessonId))
                throw new InvalidDataException("Некорректная привязка SQL-события к работе.");
        _ = db.Imports.AsNoTracking().ToArray();
        foreach (var state in db.SqlExecutionStates.AsNoTracking())
        {
            var bytes = Convert.FromBase64String(state.Base64);
            if (!profiles.Any(p => p.Id == state.StudentId && p.Role == "student") ||
                state.Hash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))
                throw new InvalidDataException("Повреждён исходный снимок SQL-попытки.");
            ElectronImport.ValidateSnapshot(state.Base64);
        }
        static bool ResultShape(SqlResult? value) => value is not null && value.Columns is not null && value.Values is not null &&
            value.Values.All(row => row is not null && row.Length == value.Columns.Length);
        foreach (var evidence in db.SqlExecutionEvidence.AsNoTracking())
        {
            var source = db.Attempts.AsNoTracking().SingleOrDefault(a => a.Id == evidence.AttemptId);
            var task = Catalog.Read(evidence.TaskJson); Catalog.Validate(task);
            var result = Wire.Read<ExecutionDto>(evidence.ResultJson);
            if (source is null || source.StudentId != evidence.StudentId || source.TaskId != task.Id || source.Code is null || source.Code.Length > 12000 ||
                source.Action != (evidence.Check ? "check" : "run") || evidence.Revision < 1 || !ResultShape(result.Output) || result.Tables is null ||
                result.Tables.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) || !ResultShape(t.Data)) ||
                result.Correct && (!evidence.Check || result.Error is not null) ||
                source.Kind != (result.Error ?? (evidence.Check ? result.Correct ? "correct" : "different" : "executed")))
                throw new InvalidDataException("Повреждены сведения об исходном состоянии SQL-попытки.");
            if (evidence.OperationId is { } operationId)
            {
                var operation = db.Operations.AsNoTracking().SingleOrDefault(o => o.StudentId == evidence.StudentId && o.Id == operationId);
                if (!Guid.TryParse(operationId, out _) || operation is null || operation.ResponseJson != evidence.ResultJson)
                    throw new InvalidDataException("SQL-попытка не соответствует квитанции операции.");
            }
        }
        var lessons = db.LessonSessions.AsNoTracking().ToArray();
        foreach (var lesson in lessons)
        {
            lesson.Validate();
            if (!profiles.Any(p => p.Id == lesson.TeacherId && p.Role == "teacher"))
                throw new InvalidDataException("У занятия отсутствует преподаватель.");
        }
        foreach (var member in db.LessonParticipants.AsNoTracking())
        {
            var lesson = lessons.SingleOrDefault(s => s.Id == member.SessionId);
            if (lesson is null || !profiles.Any(p => p.Id == member.StudentId && p.Role == "student") ||
                member.JoinedAt < (lesson.StartedAt ?? lesson.CreatedAt) || member.LastSeenAt < member.JoinedAt ||
                member.GroupNameAtEntry.Length > 200)
                throw new InvalidDataException("Некорректный участник занятия.");
        }
        foreach (var issued in db.LessonSqlTasks.AsNoTracking())
        {
            var lesson = lessons.SingleOrDefault(s => s.Id == issued.LessonId);
            Task(issued.TaskId, issued.Json);
            foreach (var alternative in Wire.Read<string[]>(issued.AlternativesJson)) Task(issued.TaskId, alternative);
            if (lesson is null || issued.IssuedAt < lesson.CreatedAt || issued.Position < 0 || issued.Revision < 1)
                throw new InvalidDataException("Некорректная выдача задания на занятие.");
        }
        foreach (var work in db.LessonSqlWorks.AsNoTracking())
        {
            var definition = Task(work.TaskId, work.Json);
            if (work.PreviousJson is not null) Task(work.TaskId, work.PreviousJson);
            var policy = db.LessonSqlTasks.AsNoTracking().SingleOrDefault(t => t.LessonId == work.LessonId && t.TaskId == work.PolicyTaskId);
            var lesson = lessons.SingleOrDefault(s => s.Id == work.LessonId);
            if (policy is null || lesson is null || work.Project != definition.Project || work.RequireNewSolution != policy.RequireNewSolution || work.CreatedAt < lesson.CreatedAt ||
                !db.LessonParticipants.Any(p => p.SessionId == work.LessonId && p.StudentId == work.StudentId) ||
                work.TaskId != policy.TaskId && (definition.Project is null || definition.Project != Catalog.Read(policy.Json).Project) &&
                    (work.RequireNewSolution || work.PreviousJson != work.Json) ||
                Wire.Read<long[]>(work.HintsJson).Length > definition.Hints.Length || work.Checks < 0 || work.Errors < 0 || work.Errors > work.Checks || work.ActiveSeconds < 0)
                throw new InvalidDataException("Некорректная SQL-работа занятия.");
        }
        foreach (var snapshot in db.LessonSqlSnapshots.AsNoTracking())
        {
            if (!db.LessonParticipants.Any(p => p.SessionId == snapshot.LessonId && p.StudentId == snapshot.StudentId))
                throw new InvalidDataException("Некорректный владелец снимка SQL-проекта занятия.");
            ElectronImport.ValidateSnapshot(snapshot.Base64);
        }
        void ElectronicTask(string title, string prompt, string? starter)
        {
            if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Any(char.IsControl) || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 32000)
                throw new InvalidDataException("Некорректное электронное задание.");
            if (starter is not null) ElectronicsProjectFormat.Inspect(starter);
        }
        foreach (var task in db.ElectronicsTasks.AsNoTracking())
        {
            ElectronicTask(task.Title, task.Prompt, task.StarterJson);
            if (task.Revision < 1 || task.CreatedAt < 1 || task.UpdatedAt < task.CreatedAt)
                throw new InvalidDataException("Некорректные даты/редакция электронного задания.");
        }
        foreach (var revision in db.ElectronicsTaskRevisions.AsNoTracking())
        {
            ElectronicTask(revision.Title, revision.Prompt, revision.StarterJson);
            if (revision.Revision < 1 || revision.At < 1) throw new InvalidDataException("Некорректная редакция электронного задания.");
        }
        foreach (var issued in db.LessonElectronicsTasks.AsNoTracking())
        {
            ElectronicTask(issued.Title, issued.Prompt, issued.StarterJson);
            var lesson = lessons.SingleOrDefault(s => s.Id == issued.LessonId);
            if (lesson is null || issued.Revision < 1 || issued.Position < 0 || issued.IssuedAt < lesson.CreatedAt)
                throw new InvalidDataException("Некорректная выдача электронного задания.");
        }
        var electronicAssignments = db.ElectronicsAssignments.AsNoTracking().ToDictionary(a => a.Id);
        foreach (var assignment in electronicAssignments.Values)
        {
            ElectronicsProjectFormat.Inspect(assignment.InitialProjectJson);
            var lesson = lessons.SingleOrDefault(s => s.Id == assignment.LessonId);
            if (lesson is null || assignment.OpenedAt < lesson.CreatedAt || assignment.StateVersion < 1 || !Guid.TryParseExact(assignment.Id, "N", out _) ||
                !db.LessonParticipants.Any(p => p.SessionId == assignment.LessonId && p.StudentId == assignment.StudentId) ||
                assignment.State is not ("draft" or "awaiting-review" or "accepted" or "changes-requested" or "rejected"))
                throw new InvalidDataException("Некорректная электронная работа занятия.");
        }
        void ElectronicOwner(string? assignmentId, string studentId, long at)
        {
            if (assignmentId is not null && (!electronicAssignments.TryGetValue(assignmentId, out var assignment) || assignment.StudentId != studentId || at < assignment.OpenedAt))
                throw new InvalidDataException("Электронная копия относится к другой работе.");
        }
        foreach (var row in db.ElectronicsWorks.AsNoTracking())
        {
            ElectronicOwner(row.AssignmentId, row.StudentId, row.UpdatedAt);
            var metadata = ElectronicsProjectFormat.Inspect(row.ProjectJson);
            if (row.Sequence < 1 || row.UpdatedAt < 1 || !profiles.Any(p=>p.Id==row.StudentId&&p.Role=="student") || !Guid.TryParse(row.ClientId,out _) || metadata.Name != row.Name || metadata.Parts != row.Parts || metadata.Wires != row.Wires || row.ProjectHash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.ProjectJson))))
                throw new InvalidDataException("Повреждена электронная работа.");
        }
        foreach (var row in db.ElectronicsReports.AsNoTracking())
        {
            ElectronicOwner(row.AssignmentId, row.StudentId, row.At);
            var metadata = ElectronicsProjectFormat.Inspect(row.ProjectJson);
            if (row.At < 1 || row.ExpectedAssignmentVersion is < 1 || !profiles.Any(p=>p.Id==row.StudentId&&p.Role=="student") || !Guid.TryParse(row.Id,out _) || !Guid.TryParse(row.ClientId,out _) || !ElectronicsProjectFormat.ReportKinds.Contains(row.Kind) || row.Detail.Length > 16000 || metadata.Name != row.Name || row.ProjectHash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.ProjectJson))))
                throw new InvalidDataException("Повреждён результат по электронике.");
        }
        foreach (var history in db.ElectronicsReviews.AsNoTracking().ToArray().GroupBy(r => new { r.StudentId, r.ReportId }))
        {
            var report = db.ElectronicsReports.AsNoTracking().SingleOrDefault(r => r.StudentId == history.Key.StudentId && r.Id == history.Key.ReportId);
            string? previous = null; var sequence = 0;
            foreach (var review in history.OrderBy(r => r.Sequence))
            {
                if (report?.Kind != "submitted" || review.At < report.At || review.Sequence != ++sequence || review.PreviousReviewId != previous ||
                    !Guid.TryParseExact(review.Id, "N", out _) || !profiles.Any(p => p.Id == review.TeacherId && p.Role == "teacher") ||
                    review.Action is not ("accepted" or "changes-requested" or "rejected" or "allow-resubmit") || review.Comment.Length > 8000 ||
                    review.Grade is < 1 or > 5 || review.Action is "accepted" or "rejected" && review.Grade is null ||
                    review.Action == "allow-resubmit" && review.Grade is not null || review.RequestHash.Length != 64 || review.RequestHash.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Повреждена история проверки электронной работы.");
                previous = review.Id;
            }
        }
    }

    static TaskDefinition Task(string id, string json)
    {
        var task = Catalog.Read(json);
        Catalog.Validate(task);
        if (task.Id != id)
            throw new InvalidDataException("Идентификатор задания не совпадает с содержимым.");
        return task;
    }
}
