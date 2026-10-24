using System.IO.Pipes;
using System.Security.Principal;

namespace AudioPanel.Services;

/// <summary>A per-user instance lock and a current-user-only activation pipe.</summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listener;
    private bool _disposed;

    public SingleInstanceService(string? testScope = null)
    {
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = $"AudioPanel-{userId}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}" +
            (testScope is null ? "" : $"-{testScope}");
        _mutex = new Mutex(false, $"Local\\{_pipeName}");
        try { IsPrimary = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public bool IsPrimary { get; }
    public void StartListening(Action activate)
    {
        if (!IsPrimary || _listener is not null) return;
        var token = _lifetime.Token;
        _listener = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    var request = new byte[1];
                    if (await server.ReadAsync(request, token) == 1 && request[0] == 1) activate();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (System.IO.IOException)
                {
                    try { await Task.Delay(100, token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    public async Task<bool> ActivateExistingAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(new byte[] { 1 }, timeout.Token);
            await client.FlushAsync(timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is System.IO.IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _lifetime.Dispose();
    }
}
