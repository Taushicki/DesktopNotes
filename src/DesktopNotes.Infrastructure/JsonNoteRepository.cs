using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNotes.Application;
using DesktopNotes.Domain;

namespace DesktopNotes.Infrastructure;

public sealed class UnsupportedStorageVersionException(int version) : Exception($"Версия хранилища {version} не поддерживается. Обновите DesktopNotes.");

public sealed class JsonNoteRepository(string directory) : INoteRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path = Path.Combine(directory, "notes.json");
    private bool _primaryValid;
    private sealed record Document(int Version, List<Note> Notes, List<NoteGroup>? Groups = null, List<NoteLink>? Links = null);

    public async Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path) && !File.Exists(_path + ".bak")) return new LoadResult([]);
        try
        {
            var snapshot = await ReadAsync(_path, cancellationToken);
            _primaryValid = true;
            return new LoadResult(snapshot);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            try
            {
                var backup = await ReadAsync(_path + ".bak", cancellationToken);
                return new LoadResult(backup, "Основной файл повреждён или недоступен. Заметки восстановлены из резервной копии.");
            }
            catch (Exception backupError) when (backupError is JsonException or InvalidDataException or IOException)
            {
                throw new InvalidDataException("Не удалось прочитать заметки и резервную копию. Исходные файлы сохранены; приложение не будет их перезаписывать.", new AggregateException(ex, backupError));
            }
        }
    }

    private static async Task<WorkspaceSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Пустое хранилище.");
        if (document.Version is not (1 or 2)) throw new UnsupportedStorageVersionException(document.Version);
        if (document.Notes is null) throw new InvalidDataException("Отсутствует список заметок.");
        if (document.Version == 2 && (document.Groups is null || document.Links is null))
            throw new InvalidDataException("Отсутствуют группы или связи.");
        var snapshot = new WorkspaceSnapshot
        {
            Notes = document.Notes,
            Groups = document.Version == 1 ? [] : document.Groups!,
            Links = document.Version == 1 ? [] : document.Links!
        };
        snapshot.Validate();
        return snapshot;
    }

    public Task SaveAsync(IReadOnlyList<Note> notes, CancellationToken cancellationToken = default) =>
        SaveAsync(new WorkspaceSnapshot { Notes = notes }, cancellationToken);

    public async Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(2, snapshot.Notes.ToList(), snapshot.Groups.ToList(), snapshot.Links.ToList()), Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            if (File.Exists(_path) && _primaryValid) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path, true);
            _primaryValid = true;
            if (!File.Exists(_path + ".bak")) File.Copy(_path, _path + ".bak");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
