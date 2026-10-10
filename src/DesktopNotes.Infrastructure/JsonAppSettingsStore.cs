using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopNotes.Application;

namespace DesktopNotes.Infrastructure;

public sealed class JsonAppSettingsStore(string directory) : IAppSettingsStore
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "settings.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new();
        try { return Read(_path); }
        catch (JsonException)
        {
            if (File.Exists(_path + ".bak")) return Read(_path + ".bak");
            throw;
        }
    }

    private static AppSettings Read(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
            ?? throw new JsonException("Settings must be an object.");
        if (!Enum.IsDefined(settings.Language) || !Enum.IsDefined(settings.MinimumLogLevel))
            throw new JsonException("Unknown settings value.");
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings, Options);
                file.Flush(true);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak");
            else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
