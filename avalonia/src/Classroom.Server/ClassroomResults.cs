using System.Globalization;
using System.Text;
using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class ClassroomResults
{
    sealed record Pupil(string Id, string Name, string GroupKey, string GroupName, bool Joined);
    static bool SqlCheck(AttemptEntity e) => e.Action == "check" || e.Action is null && e.Kind is not ("clipboard" or "capture");
    static string Name(ProfileEntity p) => string.IsNullOrWhiteSpace(p.FullName) ? p.Login : p.FullName;
    public static ResultsSummary Summarize(IEnumerable<ResultsWork> source, IEnumerable<(string Id, bool Joined)> people)
    {
        var allRows = source.ToArray(); var rows = allRows.Where(w => w.Required).ToArray(); var students = people.ToArray();
        var percentages = students.Select(p => rows.Where(r => r.StudentId == p.Id).ToArray()).Where(r => r.Length > 0).Select(r => 100d * r.Count(w => w.Completed) / r.Length).ToArray();
        return new(students.Length, students.Count(p => p.Joined), students.Count(p => allRows.Any(r => r.StudentId == p.Id && r.Started)),
            rows.Count(r => r.Completed), rows.Length, allRows.Count(r => r.State == "awaiting-review"), allRows.Count(r => r.ReviewedAt is not null),
            percentages.Length == 0 ? null : Math.Round(percentages.Average(), 1));
    }
    static ResultsWork Sql(ISqlWork? work, string student, string taskId, string title, string? lessonId, bool required, AttemptEntity[] events)
    {
        var current = lessonId is null ? work is AssignmentEntity : work is LessonSqlWorkEntity l && l.LessonId == lessonId;
        var started = current && work is not null && (work.StartedAt is not null || work.LastActivityAt is not null || work.Checks > 0 || work.ActiveSeconds > 0 || lessonId is null && work.Draft.Length > 0);
        var completed = work?.CompletedAt is not null;
        var floor = work is LessonSqlWorkEntity lesson ? lesson.CreatedAt : 0;
        var checks = events.Where(SqlCheck).OrderBy(e => e.At).ThenBy(e => e.Id).ToArray();
        // Promoted course counters may include checks belonging to a lesson. Only
        // claim the first check when this scope's recorded sequence is complete.
        int? firstFailed = current && work?.TrackingVersion > 0 && checks.Length > 0 && checks.Length == work.Checks ? checks[0].Kind == "correct" ? 0 : 1 : null;
        return new(student, "sql", taskId, null, lessonId, title, completed ? current && work is LessonSqlWorkEntity row && row.PreviousCompletedAt == row.CompletedAt ? "credited" : !current ? "credited" : "completed" : started ? "working" : "not-started",
            required, started, completed, completed && (!current || work is LessonSqlWorkEntity prior && prior.PreviousCompletedAt == prior.CompletedAt),
            current ? work?.StartedAt : null, work?.CompletedAt, current ? work?.LastActivityAt : null, current ? work?.Checks ?? 0 : 0, current ? work?.Errors ?? 0 : 0,
            current && work is not null ? Wire.Read<long[]>(work.HintsJson).Count(t => t >= floor) : 0,
            events.Count(e => e.Kind is "clipboard" or "capture"), current ? work?.ActiveSeconds ?? 0 : 0, null, null, null, firstFailed);
    }
    static ResultsWork Electronics(string student, LessonElectronicsTaskEntity task, ElectronicsAssignmentEntity? assignment,
        ElectronicsReportEntity[] reports, ElectronicsReviewEntity[] reviews, ElectronicsWorkEntity[] drafts)
    {
        var submitted = reports.Where(r => r.Kind == "submitted").OrderByDescending(r => r.At).ThenByDescending(r => r.Id).ToArray();
        ElectronicsReviewEntity? Decision(ElectronicsReportEntity r) => reviews.Where(d => d.StudentId == student && d.ReportId == r.Id && d.Action != "allow-resubmit").OrderByDescending(d => d.Sequence).FirstOrDefault();
        var latest = submitted.FirstOrDefault(); var decision = latest is null ? null : Decision(latest);
        // During resubmission the prior resolved result remains until a new decision.
        var effective = decision ?? submitted.Skip(1).Select(Decision).FirstOrDefault(d => d is not null);
        var state = latest is null ? assignment is null ? "not-started" : "working" : decision?.Action ?? "awaiting-review";
        var ownDrafts = assignment is null ? [] : drafts.Where(d => d.AssignmentId == assignment.Id).ToArray();
        var last = reports.Select(r => (long?)r.At).Concat(ownDrafts.Select(d => (long?)d.UpdatedAt)).Append(assignment?.OpenedAt).Append(effective?.At).Max();
        return new(student, "electronics", task.TaskId, assignment?.Id, task.LessonId, task.Title, state, !task.Withdrawn, assignment is not null,
            effective?.Action == "accepted", false, assignment?.OpenedAt, effective?.Action == "accepted" ? effective.At : null, last,
            submitted.Length, reports.Count(r => r.Kind is "compile-error" or "run-error"), null, null, null, effective?.Grade, latest?.Id, effective?.At);
    }
    static ResultsWork Practice(LessonSqlPracticeEntity row, AttemptEntity[] events)
    {
        var checks = events.Where(SqlCheck).OrderBy(e => e.At).ThenBy(e => e.Id).ToArray();
        return new(row.StudentId,"sql",row.TaskId,null,row.LessonId,Catalog.Read(row.Json).Title,row.CompletedAt is null?"working":"completed",
            false,true,row.CompletedAt is not null,false,row.StartedAt,row.CompletedAt,row.LastActivityAt,row.Checks,row.Errors,
            row.Partial?null:Wire.Read<long[]>(row.HintsJson).Length,events.Count(e=>e.Kind is "clipboard" or "capture"),row.Partial?null:row.ActiveSeconds,null,null,null,
            !row.Partial&&checks.Length==row.Checks&&checks.Length>0?checks[0].Kind=="correct"?0:1:null,"course",row.Partial);
    }
    public static async Task<ResultsOverview> Build(ServerState state, ClassroomDb db, string mode, string? lessonId, string? groupKey = null, string? subject = null, string? studentId = null)
    {
        if (mode is not ("lesson" or "course") || subject is not (null or "sql" or "electronics")) throw new HttpFault(400, "Некорректный вид результатов.");
        var profiles = await db.Profiles.AsNoTracking().Where(p => p.Role == "student").ToArrayAsync();
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);
        var people = new List<Pupil>(); var works = new List<ResultsWork>(); string title, note;
        ResultsGroup? emptyLessonGroup = null;
        var allEvents = await db.Attempts.AsNoTracking().Select(a => new { a.Id, a.StudentId, a.TaskId, a.LessonId, a.WorkScope, a.At, a.Action, a.Kind }).ToArrayAsync();
        var events = allEvents.Select(a => new AttemptEntity { Id = a.Id, StudentId = a.StudentId, TaskId = a.TaskId, LessonId = a.LessonId, WorkScope = a.WorkScope, At = a.At, Action = a.Action, Kind = a.Kind }).ToArray();
        var electronicAssignments = await db.ElectronicsAssignments.AsNoTracking().ToArrayAsync();
        var reports = await db.ElectronicsReports.AsNoTracking().Select(r => new { r.StudentId, r.Id, r.AssignmentId, r.Kind, r.At }).ToArrayAsync();
        var lightReports = reports.Select(r => new ElectronicsReportEntity { StudentId = r.StudentId, Id = r.Id, AssignmentId = r.AssignmentId, Kind = r.Kind, At = r.At }).ToArray();
        var reviews = await db.ElectronicsReviews.AsNoTracking().ToArrayAsync();
        var drafts = await db.ElectronicsWorks.AsNoTracking().Select(w => new ElectronicsWorkEntity { StudentId = w.StudentId, ClientId = w.ClientId, AssignmentId = w.AssignmentId, UpdatedAt = w.UpdatedAt }).ToArrayAsync();
        var electronicsTasks = await db.LessonElectronicsTasks.AsNoTracking().ToArrayAsync();
        if (mode == "lesson")
        {
            var lesson = lessonId is null ? await db.LessonSessions.AsNoTracking().SingleOrDefaultAsync(l => l.EndedAt == null) : await db.LessonSessions.AsNoTracking().SingleOrDefaultAsync(l => l.Id == lessonId);
            if (lesson is null)
                return new(mode, null, "Текущее занятие", "Занятие ещё не создано. История занятий доступна в списке.", ServerState.Now, Summarize([], []), [], [], [], []);
            lessonId = lesson.Id; title = lesson.GroupName;
            if (lesson.GroupId is { } selectedGroup) emptyLessonGroup = new(selectedGroup, lesson.GroupName, Summarize([], []));
            note = lesson.StartedAt is null ? "Занятие ещё не начато. Состав выбранной группы фиксируется при начале занятия." : lesson.RosterAtStart ? "Состав фиксируется при начале занятия; новые ученики и вошедшие из других групп добавляются отдельно." : lesson.RosterCapturedAt is null ? "Исторический состав полностью не записывался. Показаны известные участники; отсутствующих учеников восстановить по текущей группе нельзя." : "Состав зафиксирован после начала занятия. Первоначальный список мог отличаться.";
            var roster = await db.LessonRoster.AsNoTracking().Where(r => r.LessonId == lesson.Id).OrderBy(r => r.AddedAt).ThenBy(r => r.StudentId).ToArrayAsync();
            var participants = await db.LessonParticipants.AsNoTracking().Where(p => p.SessionId == lesson.Id).ToArrayAsync();
            foreach (var row in roster)
                people.Add(new(row.StudentId, row.StudentName, row.GroupKnown ? row.GroupId ?? "none" : "legacy:" + SqlSyncHash.Text(row.GroupName), row.GroupName + (row.GroupKnown ? "" : " · ID группы не записан"), participants.Any(p => p.StudentId == row.StudentId)));
            foreach (var member in participants.Where(p => people.All(r => r.Id != p.StudentId)))
            {
                var profile = profiles.SingleOrDefault(p => p.Id == member.StudentId); if (profile is null) continue;
                people.Add(new(profile.Id, Name(profile), "legacy:" + SqlSyncHash.Text(member.GroupNameAtEntry), member.GroupNameAtEntry + " · ID группы не записан", true));
            }
            foreach (var pupil in people)
            {
                var profile = profiles.Single(p => p.Id == pupil.Id);
                var available = await LessonSqlTasks.Catalogue(state, db, profile, lesson);
                foreach (var item in available.Tasks.Where(t => available.Required.Contains(t.Id)))
                {
                    available.Work.TryGetValue(item.Id, out var work);
                    works.Add(Sql(work, pupil.Id, item.Id, work is null ? item.Title : Catalog.Read(work.Json).Title, lesson.Id, true,
                        events.Where(e => e.StudentId == pupil.Id && e.TaskId == item.Id && e.LessonId == lesson.Id && e.WorkScope == "lesson:" + lesson.Id).ToArray()));
                }
                var retained = await db.LessonSqlWorks.AsNoTracking().Where(w => w.LessonId == lesson.Id && w.StudentId == pupil.Id).ToArrayAsync();
                foreach (var old in retained.Where(w => works.All(r => r.StudentId != pupil.Id || r.Subject != "sql" || r.TaskId != w.TaskId)))
                    works.Add(Sql(old, pupil.Id, old.TaskId, Catalog.Read(old.Json).Title, lesson.Id, false, events.Where(e => e.StudentId == pupil.Id && e.TaskId == old.TaskId && e.LessonId == lesson.Id && e.WorkScope == "lesson:" + lesson.Id).ToArray()));
                var practice = await db.LessonSqlPractice.AsNoTracking().Where(w=>w.LessonId==lesson.Id&&w.StudentId==pupil.Id).ToArrayAsync();
                foreach(var row in practice)works.Add(Practice(row,events.Where(e=>e.StudentId==pupil.Id&&e.TaskId==row.TaskId&&e.LessonId==lesson.Id&&(e.WorkScope is null or "course")).ToArray()));
                var streams = await db.SqlJournalStreams.AsNoTracking().Where(s=>s.StudentId==pupil.Id&&s.LessonId==lesson.Id&&s.WorkScope=="course").ToArrayAsync();
                var oldPracticeIds = events.Where(e=>e.StudentId==pupil.Id&&e.LessonId==lesson.Id&&(e.WorkScope is null or "course")).Select(e=>e.TaskId).Concat(streams.Select(s=>s.TaskId)).Distinct().Where(id=>practice.All(p=>p.TaskId!=id));
                foreach(var id in oldPracticeIds)
                {
                    var ownEvents=events.Where(e=>e.StudentId==pupil.Id&&e.TaskId==id&&e.LessonId==lesson.Id&&(e.WorkScope is null or "course")).OrderBy(e=>e.At).ThenBy(e=>e.Id).ToArray();
                    var checks=ownEvents.Where(SqlCheck).ToArray();var correct=checks.FirstOrDefault(e=>e.Kind=="correct");
                    var stream=streams.Where(s=>s.TaskId==id).OrderByDescending(s=>s.ReceivedAt).FirstOrDefault();
                    var evidenceJson=await (from a in db.Attempts.AsNoTracking() join proof in db.SqlExecutionEvidence on a.Id equals proof.AttemptId
                        where a.StudentId==pupil.Id&&a.LessonId==lesson.Id&&a.TaskId==id&&(a.WorkScope==null||a.WorkScope=="course") orderby a.At,a.Id select proof.TaskJson).FirstOrDefaultAsync();
                    works.Add(new(pupil.Id,"sql",id,null,lesson.Id,evidenceJson is null?stream?.Title??id:Catalog.Read(evidenceJson).Title,correct is null?"working":"completed",false,true,correct is not null,false,null,correct?.At,
                        ownEvents.Where(e=>e.Kind is not ("clipboard" or "capture")).Select(e=>(long?)e.At).Append(stream?.ReceivedAt).Max(),checks.Length,checks.Count(e=>e.Kind!="correct"),null,ownEvents.Count(e=>e.Kind is "clipboard" or "capture"),null,null,null,null,null,"course",true));
                }
                foreach (var task in electronicsTasks.Where(t => t.LessonId == lesson.Id && (!t.Withdrawn || electronicAssignments.Any(a => a.LessonId == lesson.Id && a.TaskId == t.TaskId && a.StudentId == pupil.Id))))
                {
                    var assignment = electronicAssignments.SingleOrDefault(a => a.LessonId == lesson.Id && a.TaskId == task.TaskId && a.StudentId == pupil.Id);
                    works.Add(Electronics(pupil.Id, task, assignment, lightReports.Where(r => assignment is not null && r.AssignmentId == assignment.Id).ToArray(), reviews, drafts));
                }
            }
        }
        else
        {
            title = "Прогресс курса и выданных работ"; note = "SQL — доступные задания курса; электроника — выданные работы известных занятий. Свободные старые схемы без назначения доступны в архиве электроники и не входят в обязательный прогресс.";
            var catalogue = (await state.Tasks(db)).Select(ServerState.Item).ToArray(); var assignments = await db.Assignments.AsNoTracking().ToArrayAsync();
            var roster = await db.LessonRoster.AsNoTracking().ToArrayAsync(); var participants = await db.LessonParticipants.AsNoTracking().ToArrayAsync();
            foreach (var profile in profiles)
            {
                people.Add(new(profile.Id, Name(profile), profile.GroupId ?? "none", profile.GroupId is { } groupIdentifier ? groups.GetValueOrDefault(groupIdentifier, "Группа удалена") : "Без группы", false));
                var own = assignments.Where(a => a.StudentId == profile.Id).ToArray();
                var available = LearningAnalytics.AvailableTaskIds(catalogue, own);
                foreach (var id in available)
                {
                    var work = own.SingleOrDefault(a => a.TaskId == id); var item = catalogue.FirstOrDefault(t => t.Id == id);
                    works.Add(Sql(work, profile.Id, id, work is null ? item?.Title ?? id : Catalog.Read(work.Json).Title, null, true,
                        events.Where(e => e.StudentId == profile.Id && e.TaskId == id && (e.WorkScope is null or "course")).ToArray()));
                }
                var lessons = roster.Where(r => r.StudentId == profile.Id).Select(r => r.LessonId).Concat(participants.Where(p => p.StudentId == profile.Id).Select(p => p.SessionId)).ToHashSet();
                foreach (var task in electronicsTasks.Where(t => lessons.Contains(t.LessonId) && (!t.Withdrawn || electronicAssignments.Any(a => a.StudentId == profile.Id && a.LessonId == t.LessonId && a.TaskId == t.TaskId))))
                {
                    var assignment = electronicAssignments.SingleOrDefault(a => a.StudentId == profile.Id && a.LessonId == task.LessonId && a.TaskId == task.TaskId);
                    works.Add(Electronics(profile.Id, task, assignment, lightReports.Where(r => assignment is not null && r.AssignmentId == assignment.Id).ToArray(), reviews, drafts));
                }
            }
        }
        var canonicalNames = people.GroupBy(p => p.GroupKey).ToDictionary(g => g.Key, g => g.First().GroupName);
        people = people.Select(p => p with { GroupName = canonicalNames[p.GroupKey] }).ToList();
        var groupRows = people.GroupBy(p => new { p.GroupKey, p.GroupName }).Select(g => new ResultsGroup(g.Key.GroupKey, g.Key.GroupName,
            Summarize(works.Where(w => subject is null || w.Subject == subject).Where(w => g.Any(p => p.Id == w.StudentId)), g.Select(p => (p.Id, p.Joined))))).ToList();
        if (mode == "course") groupRows.AddRange(groups.Where(g => groupRows.All(row => row.Key != g.Key)).Select(g => new ResultsGroup(g.Key, g.Value, Summarize([], []))));
        else if (emptyLessonGroup is not null && groupRows.All(g => g.Key != emptyLessonGroup.Key)) groupRows.Add(emptyLessonGroup);
        if (groupKey is not null) people = people.Where(p => p.GroupKey == groupKey).ToList();
        if (studentId is not null) people = people.Where(p => p.Id == studentId).ToList();
        var selectedIds = people.Select(p => p.Id).ToHashSet(); works = works.Where(w => selectedIds.Contains(w.StudentId) && (subject is null || w.Subject == subject)).ToList();
        var students = people.Select(p => new ResultsStudent(p.Id, p.Name, p.GroupKey, p.GroupName, p.Joined,
            Summarize(works.Where(w => w.StudentId == p.Id), [(p.Id, p.Joined)]), works.Where(w => w.StudentId == p.Id).Select(w => w.LastActivityAt).Max())).OrderBy(p => p.Name).ToArray();
        var difficulty = works.Where(w => w.Required).GroupBy(w => new { w.Subject, w.TaskId }).Select(g =>
        {
            var checkedRows = g.Where(w => w.Subject == "sql" ? w.Checks > 0 : w.State != "awaiting-review" && w.ReviewedAt is not null).ToArray();
            var firsts = checkedRows.Where(w => w.FirstCheckFailed is not null).ToArray();
            var unsolved = checkedRows.Count(w => !w.Completed);
            return new ResultsDifficulty(g.Key.Subject, g.Key.TaskId, g.First().Title, g.Count(), g.Count(w => w.Started), checkedRows.Length, unsolved,
                checkedRows.Length == 0 ? null : Math.Round(100d * unsolved / checkedRows.Length, 1), firsts.Length, firsts.Count(w => w.FirstCheckFailed == 1), checkedRows.Select(w => w.StudentId).Distinct().Count() < 5);
        }).OrderByDescending(t => t.UnsolvedPercent).ThenByDescending(t => t.Checked).ToArray();
        return new(mode, mode == "lesson" ? lessonId : null, title, note, ServerState.Now, Summarize(works, people.Select(p => (p.Id, p.Joined))), groupRows.OrderBy(g=>g.Name).ToArray(), students, works.ToArray(), difficulty);
    }
    static string Csv(string value)
    {
        var leading = value.TrimStart(); if (leading.Length > 0 && "=+-@".Contains(leading[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    static string At(long? at) => at is null ? "" : DateTimeOffset.FromUnixTimeMilliseconds(at.Value).ToString("O", CultureInfo.InvariantCulture);
    static async Task<ResultsSqlDetail> PracticeDetail(ClassroomDb db,string studentId,string taskId,string lessonId,int start)
    {
        if(!await db.LessonParticipants.AnyAsync(p=>p.SessionId==lessonId&&p.StudentId==studentId))throw new HttpFault(404,"Участник занятия не найден.");
        var work=await db.LessonSqlPractice.FindAsync(lessonId,studentId,taskId);
        var query=db.Attempts.AsNoTracking().Where(e=>e.StudentId==studentId&&e.TaskId==taskId&&e.LessonId==lessonId&&(e.WorkScope==null||e.WorkScope=="course"));
        var evidence=await (from a in query join proof in db.SqlExecutionEvidence on a.Id equals proof.AttemptId orderby a.At,a.Id select new{proof.TaskJson,proof.Revision}).FirstOrDefaultAsync();
        var stream=await db.SqlJournalStreams.AsNoTracking().Where(s=>s.StudentId==studentId&&s.TaskId==taskId&&s.LessonId==lessonId&&s.WorkScope=="course").OrderByDescending(s=>s.ReceivedAt).ThenBy(s=>s.Id).FirstOrDefaultAsync();
        if(work is null&&stream is null&&!await query.AnyAsync())throw new HttpFault(404,"Свободная работа этого занятия не записана.");
        var task=work is not null?Catalog.Read(work.Json):evidence is null?null:Catalog.Read(evidence.TaskJson);
        var correct=await query.Where(e=>e.Kind=="correct"&&(e.Action==null||e.Action=="check")).OrderBy(e=>e.At).ThenBy(e=>e.Id).FirstOrDefaultAsync();
        var rows=await query.OrderByDescending(e=>e.At).ThenByDescending(e=>e.Id).Skip(start).Take(101).ToArrayAsync();
        return new(task?.Title??stream?.Title??taskId,task?.Prompt??"Условие этой исторической работы не записывалось. Текущий каталог не подставлен вместо прежнего задания.",
            work?.Draft??stream?.TipCode??"",work?.AcceptedCode??correct?.Code,task?.VariantIndex,work?.Revision??evidence?.Revision,lessonId,"course",true,
            rows.Take(100).Select(e=>new LearningEvent(e.Id,e.At,e.Action??(e.Kind is "clipboard" or "capture"?e.Kind:"check"),e.Kind,e.Code,e.Explanation)).ToArray(),rows.Length>100,start+Math.Min(100,rows.Length),work?.Partial??true);
    }
    public static string Export(ResultsOverview data)
    {
        var b = new StringBuilder("Ученик;Группа;Направление;Задание;Занятие;Обязательное;Состояние;Завершено;Открыто UTC;Завершено UTC;Проверок или сдач;Ошибок;Подсказок;Обнаруженных ограничений;Активных секунд;Оценка;Контекст;Полнота\r\n");
        foreach (var row in data.Works)
        {
            var pupil = data.Students.Single(p => p.Id == row.StudentId);
            b.AppendLine(string.Join(';', new[] { pupil.Name, pupil.GroupName, row.Subject, row.Title, row.LessonId ?? "", row.Required ? "1" : "0", row.State, row.Completed ? "1" : "0", At(row.StartedAt), At(row.CompletedAt), row.Checks.ToString(CultureInfo.InvariantCulture), row.Errors.ToString(CultureInfo.InvariantCulture), row.Hints?.ToString(CultureInfo.InvariantCulture) ?? "", row.Violations?.ToString(CultureInfo.InvariantCulture) ?? "", row.ActiveSeconds?.ToString(CultureInfo.InvariantCulture) ?? "", row.Grade?.ToString(CultureInfo.InvariantCulture) ?? "", row.WorkScope=="course"&&row.LessonId is not null?"free-practice":row.LessonId is null?"course":"lesson",row.Partial?"partial":"recorded" }.Select(Csv)));
        }
        foreach (var pupil in data.Students.Where(p => data.Works.All(w => w.StudentId != p.Id)))
            b.AppendLine(string.Join(';', new[] { pupil.Name, pupil.GroupName, "", "Нет выданных работ", data.LessonId ?? "", "0", "", "0", "", "", "", "", "", "", "", "", "", "" }.Select(Csv)));
        return b.ToString();
    }
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapGet("/teacher/results/preferences", async (HttpContext ctx) => { var teacher = state.Authorize(ctx, "teacher"); return await state.Read(async db => new ResultsPreference((await db.Profiles.FindAsync(teacher.ProfileId))!.ResultsMode)); });
        api.MapPut("/teacher/results/preferences", (HttpContext ctx, ResultsPreference input) =>
        {
            var teacher = state.Authorize(ctx, "teacher"); if (input.Mode is not ("lesson" or "course")) throw new HttpFault(400, "Выберите занятие или курс.");
            return state.Write(async db => { (await db.Profiles.FindAsync(teacher.ProfileId))!.ResultsMode = input.Mode; return input; });
        });
        api.MapGet("/teacher/results", (HttpContext ctx, string? mode, string? lessonId, string? group, string? subject) =>
        { state.Authorize(ctx, "teacher"); return state.Read(db => Build(state, db, mode ?? "lesson", lessonId, group, subject)); });
        api.MapGet("/teacher/results/sql/{studentId}/{taskId}", (HttpContext ctx, string studentId, string taskId, string? lessonId, string? workScope, int? offset) =>
        {
            state.Authorize(ctx, "teacher"); var start = offset ?? 0; if (start is < 0 or > 1000000) throw new HttpFault(400, "Некорректная страница попыток.");
            return state.Read(async db =>
            {
                if(workScope is not null)
                {
                    if(workScope!="course"||lessonId is null)throw new HttpFault(400,"Для свободной практики выберите занятие.");
                    return await PracticeDetail(db,studentId,taskId,lessonId,start);
                }
                ISqlWork? work = lessonId is null ? await db.Assignments.FindAsync(studentId, taskId) : await db.LessonSqlWorks.FindAsync(lessonId, studentId, taskId);
                var profile = await db.Profiles.FindAsync(studentId) ?? throw new HttpFault(404, "Ученик не найден.");
                LessonSqlTaskEntity? policy = null;
                if (work is null && lessonId is not null)
                {
                    policy = await LessonSqlWorkspaces.Binding(db, profile, lessonId, taskId, true);
                    if (policy is not null) work = (await LessonSqlWorkspaces.Preview(db, profile, lessonId, policy)).FirstOrDefault(w => w.TaskId == taskId);
                }
                var template = work?.Json ?? policy?.Json ?? (await db.Tasks.FindAsync(taskId))?.Json ?? throw new HttpFault(404, "Условие не найдено.");
                var task = Catalog.Read(template); var scope = work is null ? lessonId is null ? "course" : "lesson:" + lessonId : ServerState.Scope(work);
                var sourceLesson = work is LessonSqlWorkEntity scoped ? scoped.LessonId : null;
                var query = db.Attempts.AsNoTracking().Where(e => e.StudentId == studentId && e.TaskId == taskId);
                query = scope == "course" ? query.Where(e => e.WorkScope == null || e.WorkScope == "course") : query.Where(e => e.LessonId == sourceLesson && e.WorkScope == scope);
                var rows = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(start).Take(101).ToArrayAsync();
                return new ResultsSqlDetail(task.Title, task.Prompt, work?.Draft ?? "", work?.AcceptedCode, work is null ? null : task.VariantIndex ?? 1, work?.Revision ?? policy?.Revision,
                    sourceLesson, scope, work is not null, rows.Take(100).Select(e => new LearningEvent(e.Id, e.At, e.Action ?? (e.Kind is "clipboard" or "capture" ? e.Kind : "check"), e.Kind, e.Code, e.Explanation)).ToArray(), rows.Length > 100, start + Math.Min(rows.Length,100));
            });
        });
        api.MapGet("/teacher/results/csv", async (HttpContext ctx, string? mode, string? lessonId, string? group, string? subject, string? studentId) =>
        {
            state.Authorize(ctx, "teacher"); var data = await state.Read(db => Build(state, db, mode ?? "lesson", lessonId, group, subject, studentId));
            return new ResultsCsv(Export(data), data.GeneratedAt);
        });
    }
}
