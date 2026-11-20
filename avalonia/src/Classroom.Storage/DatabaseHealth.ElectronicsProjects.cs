using System.Security.Cryptography;
using System.Text;
using Classroom.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Storage;

public static partial class DatabaseHealth
{
    static void ValidateFreeElectronics(ClassroomDb db, ProfileEntity[] profiles)
    {
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        static bool Date(long value) => value > 0 && value <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        static void Snapshot(string json, string hash, string name, int parts, int wires)
        {
            var metadata = ElectronicsProjectFormat.Inspect(json);
            if (hash != Hash(json) || metadata != (name, parts, wires))
                throw new InvalidDataException("Не совпадают содержимое и метаданные свободной схемы.");
        }
        var projects = db.ElectronicsProjects.AsNoTracking().ToDictionary(p => (p.StudentId, p.Id));
        var practice = db.ElectronicsPractice.AsNoTracking().ToDictionary(p => (p.StudentId, p.ProjectId, p.LessonId));
        var works = db.ElectronicsWorks.AsNoTracking().ToDictionary(w => (w.StudentId, w.ClientId));
        var reports = db.ElectronicsReports.AsNoTracking().Where(r => r.FreeProjectId != null).ToArray();
        var reviews = db.ElectronicsReviews.AsNoTracking().ToArray();
        foreach (var p in projects.Values)
        {
            ElectronicsProjectFormat.Inspect(p.InitialProjectJson);
            Snapshot(p.ProjectJson, p.ProjectHash, p.Name, p.Parts, p.Wires);
            if (!Guid.TryParseExact(p.Id, "N", out _) || !profiles.Any(s => s.Id == p.StudentId && s.Role == "student") ||
                p.CreationHash != Hash(p.InitialProjectJson) || p.Revision < 0 || p.StateVersion < 1 ||
                !Date(p.CreatedAt) || !Date(p.UpdatedAt) || p.UpdatedAt < p.CreatedAt ||
                p.Revision == 0 && p.ProjectJson != p.InitialProjectJson ||
                p.Revision > 0 && !works.Values.Any(w => w.StudentId == p.StudentId && w.FreeProjectId == p.Id && w.ProjectRevision == p.Revision && w.ProjectJson == p.ProjectJson && w.UpdatedAt == p.UpdatedAt) ||
                !practice.Keys.Any(k => k.StudentId == p.StudentId && k.ProjectId == p.Id))
                throw new InvalidDataException("Повреждён свободный проект ученика.");
            var submissions = reports.Where(r => r.StudentId == p.StudentId && r.FreeProjectId == p.Id && r.Kind == "submitted").OrderBy(r => r.At).ThenBy(r => r.Id).ToArray();
            if (submissions.Length == 0)
            {
                if (p.State != "working" || !p.CanSubmit || p.StateVersion != 1)
                    throw new InvalidDataException("У свободной схемы нет сдачи для сохранённого состояния проверки.");
                continue;
            }
            if (submissions.Any(r => r.ExpectedAssignmentVersion is null or < 1) ||
                submissions.Select(r => r.ExpectedAssignmentVersion).Distinct().Count() != submissions.Length ||
                p.StateVersion <= submissions.Max(r => r.ExpectedAssignmentVersion))
                throw new InvalidDataException("Некорректная версия разрешения сдачи свободной схемы.");
            var latest = submissions[^1];
            var history = reviews.Where(r => r.StudentId == p.StudentId && r.ReportId == latest.Id).OrderBy(r => r.Sequence).ToArray();
            var decision = history.LastOrDefault(r => r.Action != "allow-resubmit");
            var state = decision?.Action ?? "awaiting-review";
            var canSubmit = history.LastOrDefault()?.Action is "changes-requested" or "allow-resubmit";
            if (p.State != state || p.CanSubmit != canSubmit)
                throw new InvalidDataException("Состояние свободной схемы не совпадает с последней сдачей и решением.");
        }
        foreach (var p in practice.Values)
        {
            Snapshot(p.ProjectJson, p.ProjectHash, p.Name, p.Parts, p.Wires);
            var lesson = db.LessonSessions.AsNoTracking().SingleOrDefault(l => l.Id == p.LessonId);
            if (!projects.TryGetValue((p.StudentId, p.ProjectId), out var project) || lesson is null ||
                !db.LessonParticipants.Any(m => m.SessionId == p.LessonId && m.StudentId == p.StudentId) ||
                p.Revision < 0 || p.Revision > project.Revision || !Date(p.UpdatedAt) || p.UpdatedAt < Math.Max(project.CreatedAt, lesson.CreatedAt) ||
                p.OpenedAt is { } opened && (!Date(opened) || opened < lesson.CreatedAt || opened > p.UpdatedAt) ||
                p.FirstSavedAt is { } saved && (!Date(saved) || saved < Math.Max(project.CreatedAt, lesson.CreatedAt) || saved > p.UpdatedAt) ||
                p.Revision == project.Revision && p.ProjectJson != project.ProjectJson)
                throw new InvalidDataException("Повреждён снимок свободной схемы занятия.");
            if (p.Revision == 0 && p.ProjectJson != project.InitialProjectJson)
                throw new InvalidDataException("Неверное исходное состояние свободной схемы занятия.");
        }
        foreach (var w in works.Values)
        {
            if (w.FreeProjectId is null)
            {
                if (w.LessonId is not null || w.ProjectRevision is not null) throw new InvalidDataException("Неоднозначный контекст прежней электронной копии.");
                continue;
            }
            if (w.AssignmentId is not null || w.LessonId is null || w.ProjectRevision is null or < 1 ||
                !practice.TryGetValue((w.StudentId, w.FreeProjectId, w.LessonId), out var source) ||
                w.ProjectRevision > source.Revision || w.UpdatedAt > source.UpdatedAt ||
                source.FirstSavedAt is null || w.UpdatedAt < source.FirstSavedAt ||
                w.ProjectRevision == source.Revision && w.ProjectJson != source.ProjectJson)
                throw new InvalidDataException("Свободная копия потеряла исходный снимок занятия.");
        }
        foreach (var r in db.ElectronicsReports.AsNoTracking())
        {
            if (r.FreeProjectId is null)
            {
                if (r.LessonId is not null) throw new InvalidDataException("Неоднозначный контекст прежнего электронного отчёта.");
                continue;
            }
            if (r.AssignmentId is not null || r.LessonId is null ||
                !works.TryGetValue((r.StudentId, r.ClientId), out var source) || source.FreeProjectId != r.FreeProjectId || source.LessonId != r.LessonId ||
                !projects.TryGetValue((r.StudentId, r.FreeProjectId), out var project) || !Date(r.At) || r.At < project.CreatedAt ||
                r.Kind == "submitted" && r.ExpectedAssignmentVersion is null)
                throw new InvalidDataException("Сдача свободной схемы потеряла исходную копию.");
        }
        foreach (var stream in db.ElectronicsJournalStreams.AsNoTracking())
            if (works.TryGetValue((stream.StudentId, stream.ClientId), out var work) && work.FreeProjectId is not null && work.LessonId != stream.LessonId)
                throw new InvalidDataException("История свободной схемы относится к другому занятию.");
    }
}
