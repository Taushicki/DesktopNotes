using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Infrastructure;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DesktopNotesTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundtripPreservesAllStateAndMarkdown()
    {
        var repository = new JsonNoteRepository(_directory);
        var note = new Note { Title = "Русский заголовок", Markdown = "# Текст\n\n**Код** `x`", Color = NoteColor.Mint, Layer = WindowLayer.Background, X = -1200, Y = 42, Width = 480, Height = 350, IsCollapsed = true, IsHidden = true };
        await repository.SaveAsync([note]);
        var loaded = await new JsonNoteRepository(_directory).LoadAsync();
        Assert.Equal(note, Assert.Single(loaded.Notes));
        Assert.Null(loaded.Warning);
    }

    [Fact]
    public async Task CorruptPrimaryRecoversBackupAndDoesNotReplaceItWithCorruption()
    {
        var repository = new JsonNoteRepository(_directory);
        var original = new Note { Markdown = "original" };
        await repository.SaveAsync([original]);
        await repository.SaveAsync([original with { Markdown = "latest" }]);
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.json"), "{broken");
        var recoveredRepository = new JsonNoteRepository(_directory);
        var result = await recoveredRepository.LoadAsync();
        Assert.NotNull(result.Warning);
        Assert.Equal(original, Assert.Single(result.Notes));
        await recoveredRepository.SaveAsync(result.Notes);
        Assert.Equal(original, Assert.Single((await recoveredRepository.LoadAsync()).Notes));
        Assert.Contains("original", await File.ReadAllTextAsync(Path.Combine(_directory, "notes.json.bak")));
    }

    [Fact]
    public async Task BothCorruptFilesArePreserved()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "notes.json");
        await File.WriteAllTextAsync(path, "bad");
        await File.WriteAllTextAsync(path + ".bak", "also bad");
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonNoteRepository(_directory).LoadAsync());
        Assert.Equal("bad", await File.ReadAllTextAsync(path));
        Assert.Equal("also bad", await File.ReadAllTextAsync(path + ".bak"));
    }

    [Fact]
    public async Task FutureVersionIsNotSilentlyRecoveredOrOverwritten()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.json"), "{\"Version\":99,\"Notes\":[]}");
        await Assert.ThrowsAsync<UnsupportedStorageVersionException>(() => new JsonNoteRepository(_directory).LoadAsync());
    }

    [Fact]
    public async Task ActualWriteFailureIsReported()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file");
        await File.WriteAllTextAsync(blocker, "not a directory");
        await Assert.ThrowsAnyAsync<IOException>(() => new JsonNoteRepository(blocker).SaveAsync([new Note()]));
    }

    [Fact]
    public async Task VersionOneMigratesWithoutLosingNotesAndKeepsPreviousFileAsBackup()
    {
        Directory.CreateDirectory(_directory);
        var note = new Note { Title = "Старая заметка", Markdown = "# Сохранить текст" };
        var path = Path.Combine(_directory, "notes.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { Version = 1, Notes = new[] { note } }));
        var repository = new JsonNoteRepository(_directory);
        var loaded = await repository.LoadAsync();
        Assert.Equal(note, Assert.Single(loaded.Notes));
        Assert.Empty(loaded.Snapshot.Groups);
        Assert.Empty(loaded.Snapshot.Links);
        await repository.SaveAsync(loaded.Snapshot);
        using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        using var backup = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path + ".bak"));
        Assert.Equal(2, saved.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(1, backup.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task GroupsAndExtensibleArrowStylesSurviveJsonRoundtrip()
    {
        var a = new Note { IsPinned = true }; var b = new Note();
        var group = new NoteGroup { NoteIds = new[] { a.Id, b.Id }, BorderColor = "#AA33CC" };
        var link = new NoteLink { SourceId = a.Id, TargetId = b.Id, SourceSide = NoteSide.Top, Style = new LinkStyle { Width = 2.5, Pattern = LinkLinePattern.Dashed, Route = LinkRoute.Curved, Color = "#112233" } };
        var snapshot = new WorkspaceSnapshot { Notes = new[] { a, b }, Groups = new[] { group }, Links = new[] { link } };
        var repository = new JsonNoteRepository(_directory);
        await repository.SaveAsync(snapshot);
        var loaded = (await repository.LoadAsync()).Snapshot;
        Assert.True(loaded.Notes[0].IsPinned);
        Assert.Equal(group.Id, Assert.Single(loaded.Groups).Id);
        Assert.Equal(group.NoteIds, loaded.Groups[0].NoteIds);
        Assert.Equal(link, Assert.Single(loaded.Links));
    }

    [Fact]
    public async Task ExistingVersionTwoLinksWithoutSideStillLoad()
    {
        Directory.CreateDirectory(_directory);
        var a = new Note(); var b = new Note();
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 2, Notes = new[] { a, b }, Groups = Array.Empty<NoteGroup>(),
            Links = new[] { new { Id = Guid.NewGuid(), SourceId = a.Id, TargetId = b.Id, Style = new LinkStyle() } }
        }));
        var loaded = await new JsonNoteRepository(_directory).LoadAsync();
        Assert.Null(Assert.Single(loaded.Snapshot.Links).SourceSide);
        Assert.Null(loaded.Warning);
    }

    [Fact]
    public async Task InvalidReferencesCannotBeWritten()
    {
        var repository = new JsonNoteRepository(_directory);
        var invalid = new WorkspaceSnapshot { Notes = new[] { new Note() }, Links = new[] { new NoteLink { SourceId = Guid.NewGuid(), TargetId = Guid.NewGuid() } } };
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(invalid));
        Assert.False(File.Exists(Path.Combine(_directory, "notes.json")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
