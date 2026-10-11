using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace DesktopNotes.Infrastructure;

public sealed class SingleInstance : IDisposable
{
    public const string InstallationMutexName = @"Local\DesktopNotes.Installation";
    private readonly Mutex _mutex;
    private readonly Mutex? _installationMutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    public bool IsPrimary { get; }

    public SingleInstance(string? instanceName = null)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = instanceName ?? "DesktopNotes-" + user;
        // No thread-affine mutex ownership: retaining the named handle defines the primary lifetime.
        _mutex = new Mutex(false, "Global\\" + _pipeName, out var created);
        IsPrimary = created;
        if (IsPrimary && instanceName is null)
            _installationMutex = new Mutex(false, InstallationMutexName);
    }

    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000, cancellationToken);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false));
        await writer.WriteLineAsync(command.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    public void StartListening(Func<string, Task> receive)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException();
        _listener = ListenAsync(receive);
    }

    private async Task ListenAsync(Func<string, Task> receive)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                // Commands are small; bound reads even when a client does not send a newline.
                var buffer = new char[1];
                var text = new StringBuilder();
                while (text.Length < 64 && await reader.ReadAsync(buffer.AsMemory(), timeout.Token) != 0)
                {
                    if (buffer[0] == '\n') break;
                    text.Append(buffer[0]);
                }
                var command = text.ToString().Trim();
                if (command is "--new-note" or "--show") await receive(command);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public void Dispose() { _shutdown.Cancel(); _installationMutex?.Dispose(); _mutex.Dispose(); }
}
