using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Classroom.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Classroom.UiTests;

public sealed class ClassroomLoadTests(ITestOutputHelper output)
{
    const int pupilCount = 35;
    const int schemeCount = 12;
    const string workerRoute = "teacher/diagnostics/sql-worker";
    sealed record Pupil(string Id, string Login, ClassroomApi Api);
    sealed record SqlWork(Pupil Pupil, StudentTask Task, SqlTextJournal Journal, string Draft, string Answer);
    sealed record SchemeWork(Pupil Pupil, ElectronicsJournal Journal, string ReportId);
    sealed record Sample(string Operation, double Milliseconds, bool Ok);
    readonly ConcurrentQueue<Sample> measurements = new();
    int checksOutstanding;
    int peakChecksOutstanding;

    [Fact]
    public async Task ClassOfThirtyFivePreservesBothHistoriesAndTeacherResults()
    {
        await using var fixture = await LiveClassroom.Start();
        var pupils = new List<Pupil> { new(fixture.StudentId, "ui-test", fixture.Student) };
        try
        {
            var (lesson, taskId) = await CreateClass(fixture, pupils);
            using var guest = new ClassroomApi(fixture.Admin.Connection);
            Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => guest.Get<SqlWorkerDiagnostics>(workerRoute))).Status);
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => fixture.Student.Get<SqlWorkerDiagnostics>(workerRoute))).Status);
            await Task.WhenAll(pupils.Select(p => Measure("student-login", () =>
                p.Api.Login(new("student", p.Login, "synthetic-student-password")))));
            var initialMetrics = fixture.ProcessMetrics();
            var workload = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource();
            var teacher = PollTeacher(fixture.Admin, lesson.Id, stop.Token);
            SqlWork[] sqlWorks;
            SchemeWork[] schemes;
            SqlWorkerDiagnostics beforeChecks;
            try
            {
                var opened = await Task.WhenAll(pupils.Select(p => Measure("open-sql", () => p.Api.Get<StudentTask>("tasks/database"))));
                sqlWorks = pupils.Select((p, i) => PrepareSql(p, opened[i], i)).ToArray();
                await Task.WhenAll(sqlWorks.Select(work => DeliverSql(work, lesson.Id)));
                beforeChecks = await fixture.Admin.Get<SqlWorkerDiagnostics>(workerRoute);

                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var sqlChecks = sqlWorks.Select(work => Check(work, lesson.Id, start.Task)).ToArray();
                var electronicWorks = pupils.Take(schemeCount).Select((p, index) =>
                    DeliverScheme(p, lesson.Id, taskId, index, start.Task)).ToArray();
                start.SetResult();
                await Task.WhenAll(Task.WhenAll(sqlChecks), Task.WhenAll(electronicWorks));
                schemes = await Task.WhenAll(electronicWorks);
            }
            finally
            {
                stop.Cancel();
                await teacher;
                workload.Stop();
                var metrics = fixture.ProcessMetrics();
                WriteMeasurements();
                output.WriteLine($"Mixed workload wall time: {workload.Elapsed.TotalSeconds:F2} s");
                output.WriteLine($"Server peak working set: {metrics.PeakWorkingSetBytes / 1048576d:F1} MiB");
                output.WriteLine($"Server CPU during mixed window: {(metrics.ProcessorTime - initialMetrics.ProcessorTime).TotalSeconds:F2} s");
                output.WriteLine($"Peak simultaneous client SQL calls: {peakChecksOutstanding}");
            }

            Assert.Equal(pupilCount, peakChecksOutstanding);
            Assert.Contains(measurements, sample => sample.Operation == "teacher-activity" && sample.Ok);
            Assert.All(measurements, sample => Assert.True(sample.Ok, sample.Operation));
            var worker = await fixture.Admin.Get<SqlWorkerDiagnostics>(workerRoute);
            output.WriteLine($"SQL worker: peak waiting={worker.PeakWaiting}, admitted={worker.Admitted}, " +
                $"rejected={worker.Rejected}, samples={worker.WaitSamples}, p95={worker.WaitP95Milliseconds:F1} ms, max={worker.WaitMaxMilliseconds:F1} ms");
            Assert.Equal(0, worker.Running);
            Assert.Equal(0, worker.Waiting);
            Assert.Equal(pupilCount, worker.Admitted - beforeChecks.Admitted);
            Assert.Equal(beforeChecks.Rejected, worker.Rejected);
            Assert.Equal(pupilCount, worker.WaitSamples - beforeChecks.WaitSamples);
            Assert.InRange(worker.PeakWaiting, 1, pupilCount - 4);
            Assert.True(worker.WaitP95Milliseconds > 0 && worker.WaitMaxMilliseconds >= worker.WaitP95Milliseconds);
            var queue = await fixture.Admin.Get<ElectronicsReviewQueue>("teacher/reviews/electronics?lessonId=" + lesson.Id);
            Assert.Equal(schemeCount, queue.PendingCount);
            Assert.Equal(schemeCount, queue.Items.Select(item => item.StudentId).Distinct().Count());
            Assert.Equal(schemes.Select(work => work.ReportId).Order(), queue.Items.Select(item => item.ReportId).Order());
            var selected = queue.Items[0];
            var reviewPath = $"teacher/reviews/electronics/{selected.StudentId}/{selected.ReportId}";
            var review = await fixture.Admin.Get<ElectronicsReviewDetail>(reviewPath);
            Assert.Equal(schemes.Single(work => work.ReportId == selected.ReportId).Journal.Replay(), review.ProjectJson);
            await fixture.Admin.Put<ElectronicsReviewDecision>(reviewPath + "/decisions/" + Id(),
                new ElectronicsReviewRequest("accepted", "Синтетическая проверка", 5,
                    review.History.LastOrDefault()?.Id, review.AssignmentVersion));

            await Verify(fixture, lesson.Id, sqlWorks, schemes);
            await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/pause");
            Assert.All(await Task.WhenAll(pupils.Select(p => p.Api.Get<StudentLessonDto>("student/lesson"))),
                state => Assert.False(state.CanWork));
            await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/resume");
            Assert.All(await Task.WhenAll(pupils.Select(p => p.Api.Get<StudentLessonDto>("student/lesson"))),
                state => Assert.True(state.CanWork));

            await fixture.Restart();
            await fixture.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
            Assert.Equal(new(0, 0, 0, 0, 0, 0, null, null), await fixture.Admin.Get<SqlWorkerDiagnostics>(workerRoute));
            var savedLesson = (await fixture.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
            Assert.Equal(lesson.Id, savedLesson.Id);
            Assert.False(savedLesson.Activated);
            await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/restore");
            await pupils[0].Api.Login(new("student", pupils[0].Login, "synthetic-student-password"));
            Assert.True((await pupils[0].Api.Get<StudentLessonDto>("student/lesson")).CanWork);
            await Verify(fixture, lesson.Id, sqlWorks, schemes);
        }
        finally
        {
            foreach (var pupil in pupils.Skip(1)) pupil.Api.Dispose();
        }
    }

    static async Task<(LessonView Lesson, string TaskId)> CreateClass(LiveClassroom fixture, List<Pupil> pupils)
    {
        var a = await fixture.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("Нагрузка А"));
        var b = await fixture.Admin.Post<GroupDto>("teacher/groups", new GroupRequest("Нагрузка Б"));
        await fixture.Admin.Put<JsonElement>($"teacher/students/{fixture.StudentId}/group", new GroupMembershipRequest(a.Id));
        for (var i = 1; i < pupilCount; i++)
        {
            var login = $"load-{i:D2}";
            var created = await fixture.Admin.Post<JsonElement>("teacher/students",
                new CreateStudentRequest(login, "synthetic-student-password", i < 18 ? a.Id : b.Id));
            pupils.Add(new(created.GetProperty("id").GetString()!, login, new ClassroomApi(fixture.Admin.Connection)));
        }
        var previous = (await fixture.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await fixture.Admin.Post<LessonView>($"teacher/lessons/{previous.Id}/finish");
        var lesson = await fixture.Admin.Post<LessonView>("teacher/lessons",
            new CreateLessonRequest(Id(), new(a.Id, OpenToAllGroups: true, FreePractice: true)));
        await fixture.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest(["database"]));
        var taskId = Id();
        await fixture.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + taskId,
            new ElectronicsTaskRequest("Светодиод в общей схеме", "Сохраните собственный экземпляр.", CircuitProject.Blink().Serialize()));
        await fixture.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson.Id}/electronics-tasks", new ElectronicsSelectionRequest([taskId]));
        return (await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/start"), taskId);
    }

    static SqlWork PrepareSql(Pupil pupil, StudentTask task, int number)
    {
        var definition = Catalog.Data.Tasks.Single(t => t.Id == task.Id);
        var answer = (task.VariantIndex == 1 ? definition : Variants.Create(definition)[task.VariantIndex - 2]).Solution;
        var began = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var journal = SqlTextJournal.Start(task.Draft, began);
        var previous = task.Draft;
        var count = number == 0 ? 257 : 96;
        for (var i = 1; i <= count; i++)
        {
            var next = $"SELECT {i}; -- черновик {number}";
            journal = SqlTextJournal.Record(journal, previous, next, "student", "edit", began + i);
            previous = next;
        }
        return new(pupil, task, journal, previous, answer);
    }

    async Task DeliverSql(SqlWork work, string lessonId)
    {
        var api = work.Pupil.Api;
        const string draftRoute = "tasks/database/draft";
        await Measure("save-sql-draft", () => api.Put<JsonElement>(draftRoute,
            new DraftRequest(work.Draft, lessonId, work.Task.WorkScope)));
        var journal = work.Journal;
        foreach (var changes in journal.Changes.Chunk(64))
        {
            var batch = new SqlJournalBatch(journal.StreamId, lessonId, work.Task.WorkScope, work.Task.Id,
                journal.StartedAt, journal.BaseCode, journal.Changes.Length, changes);
            var ack = await Measure("sql-history-batch", () => api.Post<SqlJournalAck>("sql-journal/sync", batch));
            Assert.Equal(changes[^1].Sequence, ack.Sequence);
            if (changes[0].Sequence == 1)
                Assert.Equal(ack, await Measure("sql-history-retry", () => api.Post<SqlJournalAck>("sql-journal/sync", batch)));
        }
    }

    async Task Check(SqlWork work, string lessonId, Task start)
    {
        await start;
        var parallel = Interlocked.Increment(ref checksOutstanding);
        int old;
        do { old = Volatile.Read(ref peakChecksOutstanding); }
        while (parallel > old && Interlocked.CompareExchange(ref peakChecksOutstanding, parallel, old) != old);
        var request = new ExecuteRequest(Id(), work.Answer, true, lessonId, work.Task.WorkScope);
        try
        {
            var result = await Measure("sql-check", () => work.Pupil.Api.Post<ExecutionDto>("tasks/database/execute", request));
            Assert.True(result.Correct, result.Error);
        }
        finally { Interlocked.Decrement(ref checksOutstanding); }
        var retry = await Measure("sql-check-retry", () => work.Pupil.Api.Post<ExecutionDto>("tasks/database/execute", request));
        Assert.True(retry.Correct, retry.Error);
    }

    async Task<SchemeWork> DeliverScheme(Pupil pupil, string lessonId, string taskId, int number, Task start)
    {
        await start;
        var api = pupil.Api;
        var assignment = await Measure("open-scheme", () => api.Get<ElectronicsAssignmentDto>($"electronics/tasks/{taskId}"));
        var initial = assignment.ProjectJson;
        var project = CircuitProject.Parse(initial);
        var began = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var journal = ElectronicsJournal.Start(initial, began);
        var previous = initial;
        var count = number == 0 ? 130 : 72;
        for (var i = 1; i <= count; i++)
        {
            project.Code = $"// Схема {number}, шаг {i}\nvoid setup() {{}}\nvoid loop() {{}}";
            var next = project.Serialize();
            journal = ElectronicsJournal.Record(journal, previous, next, "edit", began + i);
            previous = next;
        }
        var client = Id();
        var report = Id();
        var submission = new ElectronicsSyncRequest(client, 1, previous,
            [new(report, "submitted", previous, "", assignment.StateVersion)], assignment.Id);
        var receipt = await Measure("submit-scheme", () => api.Put<ElectronicsSyncReceipt>("electronics/work", submission));
        Assert.Equal(new[] { report }, receipt.ReportIds);
        Assert.Equal(receipt.ReportIds, (await Measure("scheme-submission-retry",
            () => api.Put<ElectronicsSyncReceipt>("electronics/work", submission))).ReportIds);
        foreach (var changes in journal.Changes.Chunk(8))
        {
            var batch = new ElectronicsJournalBatch(journal.StreamId, client, lessonId, assignment.Id,
                journal.StartedAt, journal.BaseJson, journal.Changes.Length, changes);
            var ack = await Measure("scheme-history-batch", () => api.Post<ElectronicsJournalAck>("electronics-journal/sync", batch));
            Assert.Equal(changes[^1].Sequence, ack.Sequence);
        }
        return new(pupil, journal, report);
    }

    static async Task Verify(LiveClassroom fixture, string lessonId, SqlWork[] sql, SchemeWork[] schemes)
    {
        var data = await fixture.Admin.Get<ResultsOverview>("teacher/results?mode=lesson&lessonId=" + lessonId);
        Assert.Equal(pupilCount, data.Summary.Joined);
        Assert.Equal(new[] { 17, 18 }, data.Groups.Select(g => g.Summary.Joined).Order().ToArray());
        var sqlRows = data.Works.Where(work => work.Subject == "sql").ToArray();
        Assert.Equal(pupilCount, sqlRows.Length);
        Assert.All(sqlRows, work => { Assert.True(work.Completed); Assert.Equal(1, work.Checks); });
        Assert.Equal(schemeCount - 1, data.Summary.Pending);
        Assert.Single(data.Works, work => work.Subject == "electronics" && work.Completed && work.Grade == 5);
        var first = sql[0];
        var answer = await fixture.Admin.Get<ResultsSqlDetail>(
            $"teacher/results/sql/{first.Pupil.Id}/{first.Task.Id}?lessonId={lessonId}");
        Assert.Equal(first.Answer, answer.AcceptedCode);
        foreach (var work in sql)
        {
            var route = $"teacher/sql-journal/{work.Pupil.Id}/{work.Journal.StreamId}/state?sequence={work.Journal.Changes.Length}";
            Assert.Equal(work.Draft, (await fixture.Admin.Get<SqlJournalText>(route)).Code);
        }
        foreach (var work in schemes)
        {
            var route = $"teacher/electronics-journal/{work.Pupil.Id}/{work.Journal.StreamId}/state?sequence={work.Journal.Changes.Length}";
            Assert.Equal(work.Journal.Replay(), (await fixture.Admin.Get<ElectronicsJournalState>(route)).ProjectJson);
        }
    }

    async Task PollTeacher(ClassroomApi api, string lesson, CancellationToken stop)
    {
        // Production polls every five seconds. The faster cadence deliberately overlaps the bursts.
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Measure("teacher-cycle", async () =>
                {
                    await Measure("teacher-overview", () => api.Get<TeacherOverview>("teacher/overview"));
                    await Measure("teacher-lesson", () => api.Get<LessonDashboard>("teacher/lessons"));
                    await Measure("teacher-scheme-catalog", () => api.Get<ElectronicsTaskSummary[]>("teacher/electronics/tasks"));
                    await Measure("teacher-activity", () => api.Get<LessonActivity>($"teacher/lessons/{lesson}/activity"));
                    return await Measure("teacher-queue", () => api.Get<ElectronicsReviewQueue>("teacher/reviews/electronics"));
                });
                await Task.Delay(250, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    async Task<T> Measure<T>(string operation, Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            var result = await action();
            ok = true;
            return result;
        }
        finally { measurements.Enqueue(new(operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ok)); }
    }

    void WriteMeasurements()
    {
        foreach (var group in measurements.GroupBy(sample => sample.Operation).OrderBy(g => g.Key))
        {
            var milliseconds = group.Select(s => s.Milliseconds).Order().ToArray();
            double Percentile(double rank) => milliseconds[(int)Math.Ceiling(milliseconds.Length * rank) - 1];
            output.WriteLine($"{group.Key}: {milliseconds.Length} calls, {group.Count(s => !s.Ok)} errors; " +
                $"p50={Percentile(0.50):F1}, p95={Percentile(0.95):F1}, max={milliseconds[^1]:F1} ms");
        }
    }

    static string Id() => Guid.NewGuid().ToString("N");
}
