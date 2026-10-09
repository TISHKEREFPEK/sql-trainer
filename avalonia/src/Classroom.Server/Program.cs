using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Classroom.Contracts;
using Classroom.Server;
using Microsoft.AspNetCore.RateLimiting;

var dataDir = Path.GetFullPath(Environment.GetEnvironmentVariable("CLASSROOM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "teacher"));
Directory.CreateDirectory(dataDir);
using var instanceLease = new FileStream(Path.Combine(dataDir, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var settingsPath = Path.Combine(dataDir, "server-settings.json");
var settings = File.Exists(settingsPath) ? Wire.Read<ServerSettings>(File.ReadAllText(settingsPath)) : new ServerSettings();
var address = IPAddress.Parse(settings.Address);
if (settings.Port is < 1024 or > 65535)
    throw new InvalidDataException("Некорректный порт сервера.");
var certificatePath = Path.Combine(dataDir, "server.pfx");
if (!File.Exists(certificatePath))
{
    using var rsa = RSA.Create(3072);
    var req = new CertificateRequest("CN=SQL-Classroom", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    var usage = new OidCollection
    {
        new("1.3.6.1.5.5.7.3.1")
    };
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usage, true));
    using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    File.WriteAllBytes(certificatePath, cert.Export(X509ContentType.Pfx));
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(certificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null);
var info = new ConnectionSettings($"https://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString())}:{settings.Port}", Convert.ToHexString(SHA256.HashData(certificate.RawData)));
File.WriteAllText(Path.Combine(dataDir, "connection.json"), Wire.Write(info));
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
    options.Listen(address, settings.Port, listen => listen.UseHttps(certificate));
    if (!IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
        options.Listen(IPAddress.Loopback, settings.Port, listen => listen.UseHttps(certificate));
});
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var workerPath = Environment.GetEnvironmentVariable("CLASSROOM_WORKER") ?? Path.Combine(AppContext.BaseDirectory, "worker", OperatingSystem.IsWindows() ? "Classroom.Worker.exe" : "Classroom.Worker");
var worker = new WorkerRunner(Path.GetFullPath(workerPath));
var state = new ServerState(Path.Combine(dataDir, "classroom.sqlite"), worker);
await state.Initialize();
var importIndex = Array.IndexOf(args, "--import-electron");
if (importIndex >= 0)
{
    if (importIndex + 1 >= args.Length)
        throw new InvalidDataException("Укажите путь к classroom.sqlite после --import-electron.");
    var source = Path.GetFullPath(args[importIndex + 1]);
    var preview = Classroom.Storage.ElectronImport.Preview(source, "cli");
    await using var db = state.Db();
    await Classroom.Storage.ElectronImport.Commit(db, source, preview.SourceHash, state.Backups);
    Console.WriteLine($"Electron import: {preview.Students} profiles, {preview.Assignments} assignments, {preview.Projects} project snapshots.");
    if (args.Contains("--import-only"))
        return;
}

var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        // Native clients need no cross-origin browser access.
        if (context.Request.Headers.ContainsKey("Origin"))
            throw new HttpFault(403, "Запросы браузера не разрешены.");
        await next(context);
    }
    catch (Exception e)
    {
        var status = e is HttpFault fault ? fault.Status : e is InvalidDataException or System.Text.Json.JsonException or FormatException ? 400 : 500;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ApiError(status == 500 ? "Не удалось выполнить действие." : e.Message));
        if (status == 500)
            app.Logger.LogError("Server operation failed: {Type}", e.GetType().Name);
    }
});
app.UseRateLimiter();
Routes.Map(app, state, dataDir, worker);
app.MapPost("/api/v1/teacher/stop", (HttpContext context) =>
{
    state.Authorize(context, "teacher");
    state.Stopping = true;
    app.Lifetime.StopApplication();
    return new
    {
        ok = true
    };
});
if (Console.IsInputRedirected)
    _ = Task.Run(async () =>
    {
        while (await Console.In.ReadLineAsync()is { } command)
            if (command == "stop")
            {
                state.Stopping = true;
                app.Lifetime.StopApplication();
                break;
            }
    });
using var daily = new PeriodicTimer(TimeSpan.FromHours(1));
var maintenance = Task.Run(async () =>
{
    try
    {
        while (await daily.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            await state.Gate.WaitAsync(app.Lifetime.ApplicationStopping);
            try
            {
                if (state.Backups.List().All(b => DateTimeOffset.FromUnixTimeMilliseconds(b.CreatedAt).UtcDateTime.Date != DateTime.UtcNow.Date))
                    state.Backups.Create("daily");
            }
            finally
            {
                state.Gate.Release();
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
});
await app.RunAsync();
await maintenance;
