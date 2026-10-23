using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.UiTests;

internal sealed class LiveClassroom : IAsyncDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "classroom-ui-live-" + Guid.NewGuid());
    Process? process;
    public ClassroomApi Admin { get; private set; } = null!;
    public ClassroomApi Student { get; private set; } = null!;
    public string StudentId { get; private set; } = "";
    public PairingAnnouncement Pairing => Wire.Read<PairingAnnouncement>(File.ReadAllText(Path.Combine(directory, "pairing.json")));

    public static async Task<LiveClassroom> Start()
    {
        var fixture = new LiveClassroom();
        try { await fixture.Initialize(); return fixture; }
        catch { await fixture.DisposeAsync(); throw; }
    }

    async Task Initialize()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Classroom.sln"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Classroom.sln");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        Directory.CreateDirectory(directory);
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await File.WriteAllTextAsync(Path.Combine(directory, "server-settings.json"), Wire.Write(new ServerSettings("127.0.0.1", port)));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CLASSROOM_DOTNET") ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, $"src/Classroom.Server/bin/{configuration}/net10.0/Classroom.Server.dll"));
        start.Environment["CLASSROOM_DATA"] = directory;
        start.Environment["CLASSROOM_WORKER"] = Path.Combine(root.FullName, $"src/Classroom.Worker/bin/{configuration}/net10.0/Classroom.Worker.dll");
        process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        for (var i = 0; i < 150; i++)
        {
            if (process.HasExited) throw new IOException("Synthetic server exited during startup.");
            var path = Path.Combine(directory, "connection.json");
            if (File.Exists(path))
            {
                var api = new ClassroomApi(Wire.Read<ConnectionSettings>(await File.ReadAllTextAsync(path)));
                try { await api.Get<StatusDto>("status"); Admin = api; break; }
                catch (HttpRequestException) { api.Dispose(); }
            }
            await Task.Delay(100);
        }
        if (Admin is null) throw new TimeoutException("Synthetic server did not start.");
        await Admin.Post<JsonElement>("setup", new PasswordRequest("synthetic-teacher-password"));
        await Admin.Login(new("teacher", "", "synthetic-teacher-password"));
        var created = await Admin.Post<JsonElement>("teacher/students", new CreateStudentRequest("ui-test", "synthetic-student-password", null));
        StudentId = created.GetProperty("id").GetString()!;
        Student = new(Admin.Connection);
        await Student.Login(new("student", "ui-test", "synthetic-student-password"));
    }

    public async ValueTask DisposeAsync()
    {
        if (process is not null && !process.HasExited)
        {
            await process.StandardInput.WriteLineAsync("stop");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            await process.WaitForExitAsync(timeout.Token);
        }
        process?.Dispose(); Student?.Dispose(); Admin?.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
