using Avalonia.Controls;
using Avalonia.Threading;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;

namespace Classroom.Desktop;

public sealed class StudentReviewPage : UserControl, IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi studentApi;
    readonly ContentControl body = new();
    readonly TextBox code = Ui.Input();
    readonly TextBlock message = Ui.Text("");
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    ClassroomApi? reviewApi;
    ElectronicsReviewQueueView? queue;
    bool disposed, checking;

    public StudentReviewPage(MainWindow owner, ClassroomApi studentApi)
    {
        this.owner = owner; this.studentApi = studentApi;
        code.Watermark = "Одноразовый код преподавателя"; code.MaxLength = 32; code.Width = 280;
        Content = body; ShowEntry();
        timer.Tick += async (_, _) =>
        {
            if (disposed || checking || reviewApi is null) return;
            checking = true;
            try { await reviewApi.Get<ReviewAccessStatus>("review-access/status"); }
            catch (Exception e) when (e is ApiException or HttpRequestException or OperationCanceledException or ObjectDisposedException)
            {
                await End();
                message.Text = "Проверка приостановлена: нет связи или доступ завершён. Вернитесь к работе ученика либо введите новый код.";
                ShowEntry();
            }
            finally { checking = false; }
        };
    }
    void ShowEntry()
    {
        if (disposed) return;
        body.Content = Ui.Card(Ui.Stack(Ui.Text("Проверка преподавателя", 22), Ui.Text("Код открывает ожидающие проверки работы только этого ученика."), code,
            Ui.Button("Открыть проверку", () => owner.Safe(Start)), message));
    }
    async Task Start()
    {
        var opened = await studentApi.StartReview(code.Text ?? ""); code.Text = "";
        if (disposed) { await RevokeAndDispose(opened.Api); return; }
        reviewApi = opened.Api; queue = new(owner, reviewApi, restrictedReview: true);
        try { await queue.Load(); }
        catch { await End(); throw; }
        if (disposed || reviewApi is null) return;
        var root = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        root.Children.Add(Ui.Row(Ui.Text("Проверка · " + opened.Session.StudentName, 20), Ui.Button("Завершить проверку", async () => { await End(); owner.ClosePage(); })));
        Grid.SetRow(queue, 1); root.Children.Add(queue); body.Content = root; timer.Start();
    }
    static async Task RevokeAndDispose(ClassroomApi api)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await api.EndReview(timeout.Token); }
        catch (Exception e) when (e is ApiException or HttpRequestException or OperationCanceledException) { }
        finally { api.Dispose(); }
    }
    async Task End()
    {
        timer.Stop(); queue?.Dispose(); queue = null;
        var api = reviewApi; reviewApi = null;
        if (!disposed) body.Content = null;
        if (api is not null) await RevokeAndDispose(api);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        _ = End(); // Controls/runner stop synchronously; bounded revocation finishes in the background.
    }
}
