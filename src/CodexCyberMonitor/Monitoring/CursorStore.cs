using System.Text.Json;

namespace CodexCyberMonitor.Monitoring;

internal sealed class CursorStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _initializedMarkerPath;
    private readonly Dictionary<string, PersistedCursor> _cursors =
        new(StringComparer.OrdinalIgnoreCase);

    public CursorStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "cursors.json");
        _initializedMarkerPath = Path.Combine(stateDirectory, "baseline-v1.initialized");
        Load();
    }

    public bool IsInitialized => File.Exists(_initializedMarkerPath);

    public bool TryGet(string identity, out PersistedCursor? cursor)
    {
        return _cursors.TryGetValue(identity, out cursor);
    }

    public void Update(IEnumerable<FileCursorState> states)
    {
        foreach (var state in states)
        {
            _cursors[state.Identity] = new PersistedCursor(
                state.CreationTimeUtcTicks,
                state.CommittedOffset);
        }
    }

    public void Save()
    {
        var tempPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(_cursors, JsonOptions);
        File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, _path, overwrite: true);
        if (!File.Exists(_initializedMarkerPath))
        {
            File.WriteAllText(
                _initializedMarkerPath,
                DateTimeOffset.Now.ToString("o"),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PersistedCursor>>(json);
            if (loaded is null)
            {
                return;
            }

            foreach (var pair in loaded)
            {
                _cursors[pair.Key] = pair.Value;
            }
        }
        catch (JsonException)
        {
            var backupPath = _path + $".invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, backupPath, overwrite: true);
        }
    }
}
