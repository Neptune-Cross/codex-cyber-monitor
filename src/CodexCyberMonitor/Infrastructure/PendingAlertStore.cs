using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexCyberMonitor.Domain;

namespace CodexCyberMonitor.Infrastructure;

internal sealed class PendingAlertStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingAlertDto> _items =
        new(StringComparer.Ordinal);

    public PendingAlertStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "pending-alerts.json");
        Load();
    }

    public IReadOnlyList<CodexEventRecord> GetPendingRecords()
    {
        lock (_gate)
        {
            return _items.Values
                .OrderBy(item => item.ObservedAt)
                .Select(item => new CodexEventRecord(
                    Enum.TryParse<CodexEventKind>(item.Kind, out var kind)
                        ? kind
                        : CodexEventKind.CyberBlock,
                    item.ObservedAt,
                    item.SourceTimestamp,
                    item.TurnReference,
                    item.Result,
                    item.Detail,
                    item.SourceFileName,
                    item.SourceIdentity,
                    item.LineOffset,
                    IsCyber: true,
                    IsTest: false))
                .ToArray();
        }
    }

    public void Add(CodexEventRecord record)
    {
        if (!record.IsCyber || record.IsTest)
        {
            return;
        }

        var id = ComputeId(record);
        var item = new PendingAlertDto(
            id,
            record.Kind.ToString(),
            record.ObservedAt,
            record.SourceTimestamp,
            record.ShortTurnId,
            record.Result,
            record.Detail,
            record.SourceFileName,
            record.SourceIdentity,
            record.LineOffset);

        lock (_gate)
        {
            var hadPrevious = _items.TryGetValue(id, out var previous);
            _items[id] = item;
            try
            {
                SaveLocked();
            }
            catch
            {
                if (hadPrevious && previous is not null)
                {
                    _items[id] = previous;
                }
                else
                {
                    _items.Remove(id);
                }
                throw;
            }
        }
    }

    public void Acknowledge(IEnumerable<CodexEventRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ids = records
            .Where(record => !record.IsTest)
            .Select(ComputeId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            // 先在同一把锁内验证整批记录均已 durable Add，再执行删除。
            // Add 失败期间 UI 仍可能已经展示记录；此时确认必须整体失败，
            // 不能把“尚未持久化”误当成“已经确认或无需确认”。
            if (ids.Any(id => !_items.ContainsKey(id)))
            {
                throw new InvalidOperationException("待确认警告尚未完成持久化，请稍后重试。");
            }

            var removed = new List<KeyValuePair<string, PendingAlertDto>>(ids.Length);
            foreach (var id in ids)
            {
                var item = _items[id];
                _items.Remove(id);
                removed.Add(new KeyValuePair<string, PendingAlertDto>(id, item));
            }

            try
            {
                SaveLocked();
            }
            catch
            {
                foreach (var pair in removed)
                {
                    _items[pair.Key] = pair.Value;
                }
                throw;
            }
        }
    }

    private static string ComputeId(CodexEventRecord record)
    {
        var raw = $"{record.SourceIdentity}|{record.LineOffset}|{record.Kind}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
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
            var loaded = JsonSerializer.Deserialize<List<PendingAlertDto>>(json);
            if (loaded is null)
            {
                return;
            }

            foreach (var item in loaded)
            {
                _items[item.Id] = item;
            }
        }
        catch (JsonException)
        {
            var backup = _path + $".invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, backup, overwrite: true);
        }
    }

    private void SaveLocked()
    {
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(_items.Values.OrderBy(item => item.ObservedAt), JsonOptions);
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed record PendingAlertDto(
        string Id,
        string Kind,
        DateTimeOffset ObservedAt,
        string SourceTimestamp,
        string TurnReference,
        string Result,
        string Detail,
        string SourceFileName,
        string SourceIdentity,
        long LineOffset);
}
