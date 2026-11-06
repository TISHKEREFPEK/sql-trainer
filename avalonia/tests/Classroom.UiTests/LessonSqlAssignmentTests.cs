using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Domain;
using Xunit;

namespace Classroom.UiTests;

public sealed class LessonSqlAssignmentTests
{
    [Fact]
    public async Task IssuancePinsEditionAndVariantsAndEnforcesPracticeAcrossEndpoints()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var old = await f.Student.Get<StudentTask>("tasks/database");
        var definition = Catalog.Read((await f.Admin.Get<TeacherTaskDto>("teacher/tasks/database")).TaskJson);
        await f.Admin.Put<JsonElement>("teacher/tasks/database", new SaveTaskRequest(Catalog.Write(definition with { Title = "Выданная редакция" }), false));
        var edition = await f.Admin.Get<TeacherTaskDto>("teacher/tasks/database");
        var selection = new LessonSqlSelectionRequest(["database"]);
        var issued = await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", selection);
        Assert.Equal("Выданная редакция", Assert.Single(issued.Tasks).Title);
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", new LessonPolicyRequest(null, OpenToAllGroups: true));
        var catalog = await f.Student.Get<CatalogueDto>("catalog");
        Assert.Equal("database", Assert.Single(catalog.Tasks).Id); Assert.Equal(["database"], catalog.Required!);
        Assert.Equal(old.Title, catalog.Tasks[0].Title); Assert.False(catalog.FreePractice);
        var other = Catalog.Data.Tasks.First(t => t.Id != "database" && t.Project is null).Id;
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Get<StudentTask>($"tasks/{other}"))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<ExecutionDto>($"tasks/{other}/execute", new ExecuteRequest(Guid.NewGuid().ToString(), "SELECT 1", true)))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Put<JsonElement>($"tasks/{other}/draft", new DraftRequest("SELECT 1")))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Student.Post<JsonElement>($"tasks/{other}/events", new TaskEventRequest(Guid.NewGuid().ToString("N"), "opened")))).Status);
        await f.Admin.Put<JsonElement>("teacher/tasks/database", new SaveTaskRequest(Catalog.Write(definition with { Title = "Поздняя редакция" }), false));
        await f.Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("new-edition", "synthetic-new-password", null));
        using var fresh = new ClassroomApi(f.Admin.Connection);
        await fresh.Login(new("student", "new-edition", "synthetic-new-password"));
        var newTask = await fresh.Get<StudentTask>("tasks/database");
        Assert.Equal("Выданная редакция", newTask.Title); Assert.Equal(edition.Revision, newTask.Revision);
        Assert.Equal(old.Revision, (await f.Student.Get<StudentTask>("tasks/database")).Revision);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => fresh.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", selection))).Status);
        await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest([]));
        Assert.Empty((await fresh.Get<CatalogueDto>("catalog")).Tasks);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => fresh.Get<StudentTask>("tasks/database"))).Status);
        await fresh.Put<JsonElement>("tasks/database/draft", new DraftRequest("SELECT 17; -- late save"));
        var restored = await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", selection);
        Assert.Equal(issued.Tasks[0].IssuedAt, restored.Tasks[0].IssuedAt);
        Assert.Equal("Выданная редакция", restored.Tasks[0].Title);
        Assert.Equal("SELECT 17; -- late save", (await fresh.Get<StudentTask>("tasks/database")).Draft);
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", new LessonPolicyRequest(null, OpenToAllGroups: true, FreePractice: true));
        var practice = await fresh.Get<CatalogueDto>("catalog");
        Assert.True(practice.Tasks.Length > 1); Assert.Equal(["database"], practice.Required!);
        await fresh.Get<StudentTask>($"tasks/{other}");
    }

    [Fact]
    public async Task ProjectIssuanceKeepsAnExistingOlderChainAndItsRemovedLastStep()
    {
        await using var f = await LiveClassroom.Start();
        var lesson = (await f.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        var chain = Catalog.Data.Tasks.Where(t => t.Project == "library").ToArray();
        Assert.True(chain.Length > 1);
        await f.Student.Get<StudentTask>("tasks/" + chain[0].Id);
        await f.Admin.Delete<JsonElement>("teacher/tasks/" + chain[^1].Id);
        var issued = await f.Admin.Put<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks", new LessonSqlSelectionRequest([chain[0].Id]));
        Assert.Equal(chain.Length - 1, issued.Tasks.Length);
        await f.Admin.Put<LessonView>($"teacher/lessons/{lesson.Id}/policy", new LessonPolicyRequest(null, OpenToAllGroups: true));
        var catalog = await f.Student.Get<CatalogueDto>("catalog");
        Assert.Equal(chain.Select(t => t.Id).Order(), catalog.Tasks.Select(t => t.Id).Order());
        Assert.Equal(chain.Length, catalog.Required!.Length);
        Assert.Equal(chain[^1].Id, (await f.Student.Get<StudentTask>("tasks/" + chain[^1].Id)).Id);
        await f.Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("new-project", "synthetic-new-password", null));
        using var fresh = new ClassroomApi(f.Admin.Connection);
        await fresh.Login(new("student", "new-project", "synthetic-new-password"));
        Assert.DoesNotContain((await fresh.Get<CatalogueDto>("catalog")).Tasks, t => t.Id == chain[^1].Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => fresh.Get<StudentTask>("tasks/" + chain[^1].Id))).Status);
        var backup = await f.Admin.Post<JsonElement>("teacher/backups");
        await f.Admin.Post<JsonElement>("teacher/backups/" + Uri.EscapeDataString(backup.GetProperty("name").GetString()!) + "/restore");
        await f.Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        Assert.Equal(chain.Length - 1, (await f.Admin.Get<LessonSqlSelection>($"teacher/lessons/{lesson.Id}/sql-tasks")).Tasks.Length);
    }
}
