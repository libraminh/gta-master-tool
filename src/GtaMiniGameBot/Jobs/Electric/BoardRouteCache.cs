using System.Security.Cryptography;
using System.Text.Json;

namespace GtaMiniGameBot;

/// <summary>
/// Cache tuyến Water &amp; Power đã chuẩn hoá. Cache chỉ là gợi ý: tuyến lấy ra luôn phải đi qua
/// chứng chỉ tường hiện tại của <see cref="BoardPlanner"/> trước khi được chạy.
/// </summary>
internal sealed class BoardRouteCache
{
    private const int SchemaVersion = 1;
    private const int MaxEntries = 128;
    private static readonly string DefaultCachePath =
        Path.Combine(AppPaths.Root, "electric", $"board-route-cache-v{SchemaVersion}.json");

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string _path;
    private bool _dirty;

    public int Count => _entries.Count;

    private BoardRouteCache(string path = null) => _path = path;

    public static BoardRouteCache CreateEmpty(string path = null) => new(path);

    public static BoardRouteCache Load(string path = null)
    {
        path ??= DefaultCachePath;
        var cache = new BoardRouteCache(path);
        try
        {
            if (!File.Exists(path)) return cache;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
            if (file?.Version != SchemaVersion || file.Entries is null) return cache;

            foreach (var entry in file.Entries)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Key) ||
                    entry.Segments is null || entry.Segments.Length == 0) continue;
                cache._entries[entry.Key] = entry;
            }
        }
        catch
        {
            // Cache hỏng không được phép chặn job; planner lạnh sẽ dựng lại.
        }
        return cache;
    }

    public static string MakeKey(BoardFrame frame, BoardRole role, Mask wall)
    {
        var thumb = ImageOps.ResizeNearest(wall, 128, 72);
        byte[] digest = SHA256.HashData(thumb.Data);
        int Q(double value, double size) =>
            (int)Math.Round(value / Math.Max(1.0, size) * 256.0);

        return string.Join(':',
            SchemaVersion,
            frame.Width, frame.Height,
            role.StartPortSide, role.GoalPortSide,
            Q(role.StartPoint.X, frame.Width), Q(role.StartPoint.Y, frame.Height),
            Q(role.GoalHit.X, frame.Width), Q(role.GoalHit.Y, frame.Height),
            Convert.ToHexString(digest));
    }

    public bool TryGet(string key, BoardRole role, int width, int height,
                       out BoardSegment[] segments)
    {
        segments = null;
        if (!_entries.TryGetValue(key, out var entry)) return false;

        var restored = new List<BoardSegment>(entry.Segments.Length);
        PointF cursor = new(role.StartPoint.X, role.StartPoint.Y);

        for (int i = 0; i < entry.Segments.Length; i++)
        {
            var saved = entry.Segments[i];
            if (BoardKeys.Index(saved.Key) < 0) return false;

            PointF end = i == entry.Segments.Length - 1
                ? new PointF(role.GoalHit.X, role.GoalHit.Y)
                : new PointF((float)(saved.EndX01 * width), (float)(saved.EndY01 * height));

            var v = BoardKeys.Vec(saved.Key);
            double distance = (end.X - cursor.X) * v.X + (end.Y - cursor.Y) * v.Y;
            double orthogonal = Math.Abs((end.X - cursor.X) * v.Y) +
                                Math.Abs((end.Y - cursor.Y) * v.X);
            if (distance <= 1.0 || orthogonal > 3.0) return false;

            restored.Add(new BoardSegment
            {
                Key = saved.Key,
                Start = cursor,
                End = end,
                Distance = distance,
                IsGoalEntry = i == entry.Segments.Length - 1
            });
            cursor = end;
        }

        if (restored[0].Key != role.StartKey ||
            restored[^1].Key != role.GoalFinalKey) return false;

        entry.LastUsedUtc = DateTime.UtcNow;
        _dirty = true;
        segments = restored.ToArray();
        return true;
    }

    public void Put(string key, BoardPlan plan)
    {
        if (plan?.Segments is null || plan.Segments.Length == 0) return;
        int width = plan.Obstacles.Width, height = plan.Obstacles.Height;

        _entries[key] = new Entry
        {
            Key = key,
            LastUsedUtc = DateTime.UtcNow,
            Segments = plan.Segments.Select(s => new SavedSegment
            {
                Key = s.Key,
                EndX01 = s.End.X / Math.Max(1.0, width),
                EndY01 = s.End.Y / Math.Max(1.0, height)
            }).ToArray()
        };

        while (_entries.Count > MaxEntries)
        {
            string oldest = _entries.Values
                .OrderBy(x => x.LastUsedUtc)
                .Select(x => x.Key)
                .First();
            _entries.Remove(oldest);
        }
        _dirty = true;
    }

    public void SaveIfDirty()
    {
        if (!_dirty || string.IsNullOrWhiteSpace(_path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            var file = new CacheFile
            {
                Version = SchemaVersion,
                Entries = _entries.Values.OrderByDescending(x => x.LastUsedUtc).ToArray()
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, _path, true);
            _dirty = false;
        }
        catch
        {
            // Cache chỉ là tối ưu; lỗi ghi không được ảnh hưởng lượt đang chạy.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // System.Text.Json cần constructor công khai để đọc lại file; nuốt lỗi im lặng ở Load
    // sẽ làm cache biến mất vĩnh viễn thay vì báo hỏng.
    private sealed class CacheFile
    {
        public CacheFile() { }
        public int Version { get; set; }
        public Entry[] Entries { get; set; }
    }

    private sealed class Entry
    {
        public Entry() { }
        public string Key { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public SavedSegment[] Segments { get; set; }
    }

    private sealed class SavedSegment
    {
        public SavedSegment() { }
        public string Key { get; set; }
        public double EndX01 { get; set; }
        public double EndY01 { get; set; }
    }
}
