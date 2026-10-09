using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Client;
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status => status;
}

public sealed class ClassroomApi : IDisposable
{
    readonly HttpClient http;
    public ConnectionSettings Connection { get; }
    public string? Token { get; private set; }

    public ClassroomApi(ConnectionSettings connection)
    {
        var uri = new Uri(connection.Address);
        var fingerprint = connection.Fingerprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new InvalidDataException("Введите HTTPS-адрес и полный отпечаток сертификата SHA-256.");
        Connection = connection with
        {
            Address = uri.GetLeftPart(UriPartial.Authority),
            Fingerprint = fingerprint
        };
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => VerifyCertificate(cert, fingerprint)
        };
        http = new(handler)
        {
            BaseAddress = new Uri(Connection.Address + "/api/v1/"),
            Timeout = TimeSpan.FromSeconds(65)
        };
    }

    public static bool VerifyCertificate(X509Certificate2? certificate, string fingerprint)
    {
        if (certificate is null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
            return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(fingerprint));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<T> Send<T>(HttpMethod method, string route, object? body = null, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(method, route);
        if (Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body is HttpContent content)
            request.Content = content;
        else if (body is not null)
            request.Content = JsonContent.Create(body, options: Wire.Json);
        using var response = await http.SendAsync(request, cancellation);
        var text = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
        {
            string error;
            try
            {
                error = Wire.Read<ApiError>(text).Error;
            }
            catch (JsonException)
            {
                error = "Сервер отклонил запрос.";
            }

            throw new ApiException((int)response.StatusCode, error);
        }

        return Wire.Read<T>(text);
    }

    public Task<T> Get<T>(string route, CancellationToken cancellation = default) => Send<T>(HttpMethod.Get, route, null, cancellation);
    public Task<T> Post<T>(string route, object? body = null, CancellationToken cancellation = default) => Send<T>(HttpMethod.Post, route, body, cancellation);
    public Task<T> Put<T>(string route, object body, CancellationToken cancellation = default) => Send<T>(HttpMethod.Put, route, body, cancellation);
    public Task<T> Delete<T>(string route) => Send<T>(HttpMethod.Delete, route);
    public async Task<LoginDto> Login(LoginRequest input)
    {
        var result = await Post<LoginDto>("login", input);
        Token = result.Token;
        return result;
    }

    public async Task Logout()
    {
        try
        {
            await Post<JsonElement>("logout");
        }
        catch (ApiException e)when (e.Status == 401)
        {
        }
        finally
        {
            Token = null;
        }
    }

    public void Dispose() => http.Dispose();
}

// Owns the pending SQL payload, so a retry always uses the same operation id and code.
public sealed class PendingExecution(string taskId, string code, bool check)
{
    public string TaskId { get; } = taskId;
    public ExecuteRequest Request { get; } = new(Guid.NewGuid().ToString(), code, check);

    public Task<ExecutionDto> Send(ClassroomApi api) => api.Post<ExecutionDto>($"tasks/{Uri.EscapeDataString(TaskId)}/execute", Request);
}
