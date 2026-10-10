using DesktopNotes.Domain;

namespace DesktopNotes.Application;

public sealed class AutosaveCoordinator : IDisposable
{
    private readonly NoteService _notes;
    private readonly INoteRepository _repository;
    private readonly TimeSpan _delay;
    private readonly SemaphoreSlim _writer = new(1);
    private CancellationTokenSource? _pending;
    private long _revision;
    private long _savedRevision;
    private bool _disposed;
    public event Action<Exception>? SaveFailed;
    public event Action? Saved;

    public AutosaveCoordinator(NoteService notes, INoteRepository repository, TimeSpan? delay = null)
    {
        _notes = notes;
        _repository = repository;
        _delay = delay ?? TimeSpan.FromMilliseconds(500);
        _notes.Changed += Schedule;
    }

    private void Schedule()
    {
        _revision++;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_pending.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(_delay, cancellationToken); await FlushAsync(); }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> FlushAsync()
    {
        await _writer.WaitAsync();
        try
        {
            if (_savedRevision == _revision) return true;
            var revision = _revision;
            WorkspaceSnapshot snapshot = _notes.Snapshot;
            await _repository.SaveAsync(snapshot);
            _savedRevision = revision;
            Saved?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke(ex);
            return false;
        }
        finally { _writer.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notes.Changed -= Schedule;
        _pending?.Cancel();
        _pending?.Dispose();
    }
}
