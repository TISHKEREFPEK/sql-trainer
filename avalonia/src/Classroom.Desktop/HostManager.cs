using System.Diagnostics;
using Classroom.Contracts;
using Classroom.Client;

namespace Classroom.Desktop;
public sealed class HostManager
{
    Process? process;
    FileStream? instanceLease;
    public string DataDirectory { get; } = Environment.GetEnvironmentVariable("CLASSROOM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "teacher");
    public ConnectionSettings? Connection { get; private set; }
    public ServerSettings Settings => File.Exists(Path.Combine(DataDirectory, "server-settings.json")) ? Wire.Read<ServerSettings>(File.ReadAllText(Path.Combine(DataDirectory, "server-settings.json"))) : new();

    public void SaveSettings(ServerSettings settings)
    {
        if (!System.Net.IPAddress.TryParse(settings.Address, out _) || settings.Port is < 1024 or > 65535)
            throw new InvalidDataException("Введите IP-адрес интерфейса и порт 1024–65535.");
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "server-settings.json"), Wire.Write(settings));
    }

    public async Task<ClassroomApi> Start()
    {
        Directory.CreateDirectory(DataDirectory);
        try
        {
            instanceLease = new FileStream(Path.Combine(DataDirectory, "teacher-ui.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("Приложение преподавателя уже открыто. Используйте окно или значок в области уведомлений.");
        }

        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var server = Environment.GetEnvironmentVariable("CLASSROOM_SERVER") ?? Path.Combine(AppContext.BaseDirectory, "runtime", "server", "Classroom.Server" + suffix);
        var worker = Environment.GetEnvironmentVariable("CLASSROOM_WORKER") ?? Path.Combine(AppContext.BaseDirectory, "runtime", "worker", "Classroom.Worker" + suffix);
        if (!File.Exists(server))
            server = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Classroom.Server/bin/Debug/net10.0/Classroom.Server.dll"));
        if (!File.Exists(worker))
            worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Classroom.Worker/bin/Debug/net10.0/Classroom.Worker.dll"));
        if (!File.Exists(server) || !File.Exists(worker))
            throw new IOException("Не найдены сервер и процесс проверки. Соберите решение или установите полную версию преподавателя.");
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (server.EndsWith(".dll"))
        {
            info.FileName = Environment.GetEnvironmentVariable("CLASSROOM_DOTNET") ?? "dotnet";
            info.ArgumentList.Add(server);
        }
        else
            info.FileName = server;
        info.Environment["CLASSROOM_DATA"] = DataDirectory;
        info.Environment["CLASSROOM_WORKER"] = worker;
        process = Process.Start(info) ?? throw new IOException("Не удалось запустить сервер.");
        // Drain output without recording tokens, passwords or SQL in application logs.
        process.OutputDataReceived += (_, _) =>
        {
        };
        process.ErrorDataReceived += (_, _) =>
        {
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var path = Path.Combine(DataDirectory, "connection.json");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (process.HasExited)
                throw new IOException("Сервер остановился при запуске. Проверьте адрес, порт и доступ к базе.");
            if (File.Exists(path))
            {
                Connection = Wire.Read<ConnectionSettings>(File.ReadAllText(path));
                var api = new ClassroomApi(Connection with { Address = $"https://127.0.0.1:{Settings.Port}" });
                try
                {
                    await api.Get<StatusDto>("status");
                    return api;
                }
                catch (HttpRequestException)
                {
                    api.Dispose();
                }
                catch (TaskCanceledException)
                {
                    api.Dispose();
                }
            }

            await Task.Delay(250);
        }

        throw new IOException("Сервер не ответил при запуске.");
    }

    public async Task Stop(ClassroomApi? api)
    {
        if (process is null || process.HasExited)
        {
            instanceLease?.Dispose();
            instanceLease = null;
            return;
        }

        await process.StandardInput.WriteLineAsync("stop");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await process.WaitForExitAsync(timeout.Token);
        process.Dispose();
        process = null;
        instanceLease?.Dispose();
        instanceLease = null;
    }
}
