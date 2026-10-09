using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Domain;
using Xunit;

namespace Classroom.Tests;
public sealed class ServerTests
{
    [Fact]
    public async Task NetworkRolesPersistenceIdempotencyAnd35Clients()
    {
        var root = FindRoot();
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dir = Path.Combine(Path.GetTempPath(), "classroom-api-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        File.WriteAllText(Path.Combine(dir, "server-settings.json"), Wire.Write(new ServerSettings("127.0.0.1", port)));
        Process? process = null;
        var clients = new List<ClassroomApi>();
        async Task<ClassroomApi> Start()
        {
            var dotnet = Environment.GetEnvironmentVariable("CLASSROOM_DOTNET") ?? "dotnet";
            var info = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(Path.Combine(root, $"src/Classroom.Server/bin/{config}/net10.0/Classroom.Server.dll"));
            info.Environment["CLASSROOM_DATA"] = dir;
            info.Environment["CLASSROOM_WORKER"] = Path.Combine(root, $"src/Classroom.Worker/bin/{config}/net10.0/Classroom.Worker.dll");
            info.Environment["CLASSROOM_DOTNET"] = dotnet;
            process = Process.Start(info)!;
            process.OutputDataReceived += (_, _) =>
            {
            };
            process.ErrorDataReceived += (_, _) =>
            {
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            for (var i = 0; i < 100; i++)
            {
                if (process.HasExited)
                    throw new IOException("Server exited while starting.");
                var connection = Path.Combine(dir, "connection.json");
                if (File.Exists(connection))
                {
                    var api = new ClassroomApi(Wire.Read<ConnectionSettings>(File.ReadAllText(connection)));
                    try
                    {
                        await api.Get<StatusDto>("status");
                        clients.Add(api);
                        return api;
                    }
                    catch (HttpRequestException)
                    {
                        api.Dispose();
                    }
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Server did not start.");
        }

        async Task Stop()
        {
            if (process is not null && !process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop");
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                await process.WaitForExitAsync(timeout.Token);
            }

            process?.Dispose();
            process = null;
        }

        try
        {
            var admin = await Start();
            await admin.Post<JsonElement>("setup", new PasswordRequest("teacher-test-password"));
            await admin.Login(new("teacher", "", "teacher-test-password"));
            using (var wrong = new ClassroomApi(admin.Connection with { Fingerprint = new string ('0', 64) }))
                await Assert.ThrowsAsync<HttpRequestException>(() => wrong.Get<StatusDto>("status"));
            var students = new List<ClassroomApi>();
            for (var i = 0; i < 35; i++)
            {
                await admin.Post<JsonElement>("teacher/students", new CreateStudentRequest($"test-{i:D2}", "student-test-password", null));
                var student = new ClassroomApi(admin.Connection);
                clients.Add(student);
                await student.Login(new("student", $"test-{i:D2}", "student-test-password"));
                students.Add(student);
            }

            var first = students[0];
            var forbidden = await Assert.ThrowsAsync<ApiException>(() => first.Get<TeacherOverview>("teacher/overview"));
            Assert.Equal(403, forbidden.Status);
            Assert.Equal(120, (await first.Get<CatalogueDto>("catalog")).Tasks.Length);
            var task = await first.Get<StudentTask>("tasks/database");
            var publicJson = Wire.Write(task);
            foreach (var secret in new[]
            {
                "\"solution\"",
                "\"seed\"",
                "\"tableMap\"",
                "\"snapshot\""
            }

            )
                Assert.DoesNotContain(secret, publicJson);
            Assert.Empty(task.OpenedHints);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => first.Post<StudentTask>("tasks/database/hint", new HintRequest(1)))).Status);
            var hint = await first.Post<StudentTask>("tasks/database/hint", new HintRequest(0));
            Assert.Single(hint.OpenedHints);
            Assert.Equal(hint.HintOpenedAt, (await first.Post<StudentTask>("tasks/database/hint", new HintRequest(0))).HintOpenedAt);
            await first.Put<JsonElement>("tasks/database/draft", new DraftRequest("SELECT * FROM students; -- черновик"));
            var operation = new ExecuteRequest(Guid.NewGuid().ToString(), "SELECT * FROM students;", true);
            Assert.True((await first.Post<ExecutionDto>("tasks/database/execute", operation)).Correct);
            Assert.True((await first.Post<ExecutionDto>("tasks/database/execute", operation)).Correct);
            var overview = await admin.Get<TeacherOverview>("teacher/overview");
            Assert.Single(overview.Attempts, a => a.Student == "test-00");
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => first.Post<ExecutionDto>("tasks/database/execute", operation with { Code = "SELECT 2" }))).Status);
            var group = await admin.Post<GroupDto>("teacher/groups", new GroupRequest("Тестовая группа"));
            await admin.Put<JsonElement>("teacher/students/" + overview.Students.Single(s => s.Login == "test-00").Id + "/group", new GroupMembershipRequest(group.Id));
            Assert.Equal(group.Id, (await admin.Get<TeacherOverview>("teacher/overview")).Students.Single(s => s.Login == "test-00").GroupId);
            var source = Catalog.Data.Tasks[0] with
            {
                Prompt = "Обновлённое условие"
            };
            await admin.Put<JsonElement>("teacher/tasks/database", new SaveTaskRequest(Catalog.Write(source), true));
            Assert.Equal(task.Prompt, (await first.Get<StudentTask>("tasks/database")).Prompt);
            Assert.Equal(source.Prompt, (await students[1].Get<StudentTask>("tasks/database")).Prompt);
            await first.Post<JsonElement>("tasks/database/violation");
            Assert.Equal(423, (await Assert.ThrowsAsync<ApiException>(() => new PendingExecution("database", "SELECT * FROM students", false).Send(first))).Status);
            await admin.Post<JsonElement>("teacher/students/" + overview.Students.Single(s => s.Login == "test-00").Id + "/unlock");
            await first.Put<JsonElement>("theme", new ThemePreference("onyx"));
            await first.Post<ThemePreset[]>("theme-presets", new ThemePresetRequest("Личная", new("paper")));
            Assert.Empty(await students[1].Get<ThemePreset[]>("theme-presets"));
            var watch = Stopwatch.StartNew();
            var load = await Task.WhenAll(students.Select(s => new PendingExecution("columns", "SELECT name, city FROM students;", true).Send(s)));
            Assert.All(load, r => Assert.True(r.Correct, r.Error));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(65));
            var spinning = await new PendingExecution("database", "WITH RECURSIVE x(a) AS (VALUES(1) UNION ALL SELECT a+1 FROM x) SELECT SUM(a) FROM x", false).Send(first);
            Assert.Contains("время", spinning.Error);
            Assert.True((await new PendingExecution("database", "SELECT * FROM students", true).Send(first)).Correct);
            // Project authoring validates against previous steps, not an empty standalone seed.
            var lib2 = Catalog.Data.Tasks.Single(t => t.Id == "lib2");
            await admin.Put<JsonElement>("teacher/tasks/lib2", new SaveTaskRequest(Catalog.Write(lib2 with { Prompt = lib2.Prompt + " Проверка редактора." }), false));
            foreach (var baseTask in Catalog.Data.Tasks.Where(t => t.Project == "library"))
            {
                var assignment = await first.Get<StudentTask>("tasks/" + baseTask.Id);
                var definition = assignment.VariantIndex == 1 ? baseTask : Variants.Create(baseTask)[assignment.VariantIndex - 2];
                Assert.True((await new PendingExecution(baseTask.Id, definition.Solution, true).Send(first)).Correct);
            }

            await admin.Delete<JsonElement>("teacher/tasks/database");
            Assert.Contains("database", (await first.Get<CatalogueDto>("catalog")).Tasks.Select(t => t.Id));
            Assert.DoesNotContain("database", (await students[2].Get<CatalogueDto>("catalog")).Tasks.Select(t => t.Id));
            await Stop();
            admin = await Start();
            await admin.Login(new("teacher", "", "teacher-test-password"));
            var restored = new ClassroomApi(admin.Connection);
            clients.Add(restored);
            var login = await restored.Login(new("student", "test-00", "student-test-password"));
            Assert.Equal("onyx", login.Theme.Preset);
            Assert.Single(login.ThemePresets);
            var stored = await restored.Get<StudentTask>("tasks/database");
            Assert.Single(stored.OpenedHints);
            Assert.Contains("черновик", stored.Draft);
            Assert.True(stored.Completed);
            Assert.True((await restored.Post<ExecutionDto>("tasks/database/execute", operation)).Correct);
            Assert.All(Catalog.Data.Tasks.Where(t => t.Project == "library"), t => Assert.Contains(t.Id, (restored.Get<CatalogueDto>("catalog").GetAwaiter().GetResult()).Completed));
        }
        finally
        {
            await Stop();
            foreach (var client in clients)
                client.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null)
        {
            if (File.Exists(Path.Combine(path.FullName, "Classroom.sln")))
                return path.FullName;
            path = path.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
