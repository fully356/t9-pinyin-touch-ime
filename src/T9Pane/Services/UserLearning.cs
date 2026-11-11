using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace T9Pane.Services;

/// <summary>一次候选上屏的学习记录。整句按 Segments 逐段学习，单词为单段。</summary>
internal sealed record T9Selection(
    SentenceInputMode Mode,
    string CanonicalCode,
    int ConsumedCodeLength,
    string Word,
    string Pinyin,
    string MatchKind,
    IReadOnlyList<SentenceSegment> Segments);

/// <summary>用户自造词：同一纯单字组合被选够次数后收录，可直接成为解码弧。</summary>
internal sealed class LearnedCustomWord
{
    public required string Word { get; init; }
    public required string Pinyin { get; init; }
    public int Count { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

internal sealed class LearningCodeEntry
{
    public int Count { get; set; }
    public required string Pinyin { get; init; }
    public required string MatchKind { get; init; }
    public int ConsumedCodeLength { get; init; }
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>磁盘上的 learning.json 结构。</summary>
internal sealed class LearningData
{
    public int Version { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; }
    public Dictionary<string, int> Unigram { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, int>> Bigram { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, LearningCodeEntry>> CodeBoost { get; set; } = new(StringComparer.Ordinal);
    public List<LearnedCustomWord> CustomWords { get; set; } = [];
}

/// <summary>
/// 用户学习存储：unigram/bigram 频次、按输入码的选择置顶（codeBoost）、自造词。
/// ReaderWriterLockSlim 保护；落盘走防抖 + 临时文件原子替换，损坏时回退 .bak，
/// 任何 IO 失败都不阻断输入。同时实现评分模型的只读视图。
/// </summary>
internal sealed class UserLearning : ISentenceLearningView, IDisposable
{
    public const int MaxUnigram = 20_000;
    public const int MaxBigramEdges = 50_000;
    public const int MaxCodeBoostCodes = 4_096;
    public const int MaxSelectionsPerCode = 64;
    public const int MaxCustomWords = 5_000;
    public const int CustomWordThreshold = 3;

    private const int DebounceMs = 750;
    private const int MaxDirtyMs = 10_000;

    private readonly string _path;
    private readonly string _backupPath;
    private readonly TimeProvider _clock;
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly LearningData _data;
    private long _revision;
    private bool _dirty;
    private DateTime _firstDirtyUtc;
    private string? _previousWord;
    private bool _disposed;

    private UserLearning(string path, LearningData data, TimeProvider clock)
    {
        _path = path;
        _backupPath = path + ".bak";
        _data = data;
        _clock = clock;
        _timer = new System.Threading.Timer(_ => SaveIfDirty(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public long Revision
    {
        get { _gate.EnterReadLock(); try { return _revision; } finally { _gate.ExitReadLock(); } }
    }

    public static UserLearning Load(string path, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var data = ReadFile(path) ?? ReadFile(path + ".bak") ?? new LearningData();
        ClampAndEvict(data);
        return new UserLearning(path, data, clock);
    }

    private static LearningData? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var data = JsonSerializer.Deserialize<LearningData>(json, JsonOptions);
            return data;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取学习数据失败 {path}: {ex.Message}");
            return null;
        }
    }

    // ---- ISentenceLearningView：评分热路径，读锁内 O(1) 查表 ----

    public int TotalLearned
    {
        get
        {
            _gate.EnterReadLock();
            try { return _data.Unigram.Values.Sum(); }
            finally { _gate.ExitReadLock(); }
        }
    }

    public int LearnedUnigram(string word)
    {
        _gate.EnterReadLock();
        try
        {
            return _data.Unigram.TryGetValue(word, out var count) ? count : 0;
        }
        finally { _gate.ExitReadLock(); }
    }

    public int LearnedBigram(string previous, string word)
    {
        _gate.EnterReadLock();
        try
        {
            return _data.Bigram.TryGetValue(previous, out var next)
                   && next.TryGetValue(word, out var count)
                ? count
                : 0;
        }
        finally { _gate.ExitReadLock(); }
    }

    public int LearnedOutgoing(string previous)
    {
        _gate.EnterReadLock();
        try
        {
            return _data.Bigram.TryGetValue(previous, out var next) ? next.Values.Sum() : 0;
        }
        finally { _gate.ExitReadLock(); }
    }

    public IReadOnlyList<LearnedCustomWord> SnapshotCustomWords()
    {
        _gate.EnterReadLock();
        try { return _data.CustomWords.ToArray(); }
        finally { _gate.ExitReadLock(); }
    }

    public IReadOnlyList<(string Word, LearningCodeEntry Entry)> RememberedSelections(string canonicalCode)
    {
        _gate.EnterReadLock();
        try
        {
            if (!_data.CodeBoost.TryGetValue(canonicalCode, out var map))
            {
                return [];
            }

            return map.Select(pair => (pair.Key, pair.Value)).ToArray();
        }
        finally { _gate.ExitReadLock(); }
    }

    // ---- 写入 ----

    /// <summary>记录一次中文候选上屏。含兜底弧或非中文结果不应调用，改调 <see cref="BreakContext"/>。</summary>
    public void Observe(T9Selection selection)
    {
        if (_disposed)
        {
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var segments = selection.Segments is { Count: > 0 }
            ? selection.Segments.Where(s => s.Kind != SentenceArcKind.Fallback).ToList()
            : [new SentenceSegment
            {
                Start = 0,
                End = selection.ConsumedCodeLength,
                Word = selection.Word,
                Pinyin = selection.Pinyin,
                Kind = SentenceArcKind.Word
            }];
        if (segments.Count == 0)
        {
            return;
        }

        _gate.EnterWriteLock();
        try
        {
            string? previous = _previousWord;
            foreach (var segment in segments)
            {
                if (segment.Word.Length == 0)
                {
                    continue;
                }

                IncrementUnigram(segment.Word);
                if (previous is { Length: > 0 })
                {
                    IncrementBigram(previous, segment.Word);
                }

                previous = segment.Word;
            }

            _previousWord = segments[^1].Word;
            RecordCodeBoost(selection, now);
            ConsiderCustomWord(segments, now);
            MarkDirty(now);
            _revision++;
        }
        finally { _gate.ExitWriteLock(); }
    }

    /// <summary>非中文上屏（英文/符号/数字/焦点切换）后切断 bigram 上文，避免学出跨框假搭配。</summary>
    public void BreakContext()
    {
        _gate.EnterWriteLock();
        try
        {
            if (_previousWord is not null)
            {
                _previousWord = null;
                _revision++;
            }
        }
        finally { _gate.ExitWriteLock(); }
    }

    private void IncrementUnigram(string word)
    {
        _data.Unigram.TryGetValue(word, out var count);
        _data.Unigram[word] = count + 1;
    }

    private void IncrementBigram(string previous, string word)
    {
        if (!_data.Bigram.TryGetValue(previous, out var next))
        {
            next = new Dictionary<string, int>(StringComparer.Ordinal);
            _data.Bigram[previous] = next;
        }

        next.TryGetValue(word, out var count);
        next[word] = count + 1;
    }

    private void RecordCodeBoost(T9Selection selection, DateTime now)
    {
        if (string.IsNullOrEmpty(selection.CanonicalCode))
        {
            return;
        }

        if (!_data.CodeBoost.TryGetValue(selection.CanonicalCode, out var map))
        {
            map = new Dictionary<string, LearningCodeEntry>(StringComparer.Ordinal);
            _data.CodeBoost[selection.CanonicalCode] = map;
        }

        if (map.TryGetValue(selection.Word, out var existing))
        {
            existing.Count++;
            existing.LastUsedUtc = now;
        }
        else
        {
            if (map.Count >= MaxSelectionsPerCode)
            {
                // 该码候选过多，淘汰最久没用的，给新选择让位。
                var oldest = map.OrderBy(pair => pair.Value.LastUsedUtc).First().Key;
                map.Remove(oldest);
            }

            map[selection.Word] = new LearningCodeEntry
            {
                Count = 1,
                Pinyin = selection.Pinyin,
                MatchKind = selection.MatchKind,
                ConsumedCodeLength = selection.ConsumedCodeLength,
                LastUsedUtc = now
            };
        }
    }

    /// <summary>纯单字、2–8 字、完整消费的组合被选够阈值后收为自造词。</summary>
    private void ConsiderCustomWord(IReadOnlyList<SentenceSegment> segments, DateTime now)
    {
        if (segments.Count is < 2 or > 4)
        {
            return;
        }

        if (segments.Any(s => s.Kind is not (SentenceArcKind.Character or SentenceArcKind.Word)))
        {
            return;
        }

        var word = string.Concat(segments.Select(s => s.Word));
        if (word.Length is < 2 or > 8)
        {
            return;
        }

        var pinyin = string.Join(' ', segments.SelectMany(s => T9Engine.Syllables(s.Pinyin)));
        var existing = _data.CustomWords.FirstOrDefault(c => c.Word == word && c.Pinyin == pinyin);
        if (existing is not null)
        {
            existing.Count++;
            existing.LastUsedUtc = now;
            return;
        }

        // 用 codeBoost 里同词的累计选择次数判断，避免每个未登录组合都立即进词库。
        var total = _data.CodeBoost.Values
            .SelectMany(map => map)
            .Where(pair => pair.Key == word)
            .Sum(pair => pair.Value.Count);
        if (total < CustomWordThreshold)
        {
            return;
        }

        if (_data.CustomWords.Count >= MaxCustomWords)
        {
            var oldest = _data.CustomWords.OrderBy(c => c.LastUsedUtc).First();
            _data.CustomWords.Remove(oldest);
        }

        _data.CustomWords.Add(new LearnedCustomWord
        {
            Word = word,
            Pinyin = pinyin,
            Count = 1,
            LastUsedUtc = now
        });
    }

    private void MarkDirty(DateTime now)
    {
        if (!_dirty)
        {
            _firstDirtyUtc = now;
        }

        _dirty = true;
        _timer.Change(DebounceMs, Timeout.Infinite);
    }

    public void Flush() => SaveIfDirty();

    private void SaveIfDirty()
    {
        LearningData? snapshot = null;
        _gate.EnterUpgradeableReadLock();
        try
        {
            if (!_dirty)
            {
                return;
            }

            _gate.EnterWriteLock();
            try
            {
                _data.UpdatedUtc = _clock.GetUtcNow().UtcDateTime;
                ClampAndEvict(_data);
                snapshot = Clone(_data);
                _dirty = false;
            }
            finally { _gate.ExitWriteLock(); }
        }
        finally { _gate.ExitUpgradeableReadLock(); }

        Persist(snapshot);
    }

    private void Persist(LearningData snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, _path, true);
            // 写入成功后留一份最近成功副本，首次保存也有可恢复的 .bak。
            File.Copy(_path, _backupPath, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"保存学习数据失败: {ex.Message}");
        }
    }

    private static LearningData Clone(LearningData source) => new()
    {
        Version = source.Version,
        UpdatedUtc = source.UpdatedUtc,
        Unigram = new Dictionary<string, int>(source.Unigram, StringComparer.Ordinal),
        Bigram = source.Bigram.ToDictionary(
            outer => outer.Key,
            outer => new Dictionary<string, int>(outer.Value, StringComparer.Ordinal),
            StringComparer.Ordinal),
        CodeBoost = source.CodeBoost.ToDictionary(
            outer => outer.Key,
            outer => outer.Value.ToDictionary(pair => pair.Key, pair => new LearningCodeEntry
            {
                Count = pair.Value.Count,
                Pinyin = pair.Value.Pinyin,
                MatchKind = pair.Value.MatchKind,
                ConsumedCodeLength = pair.Value.ConsumedCodeLength,
                LastUsedUtc = pair.Value.LastUsedUtc
            }, StringComparer.Ordinal),
            StringComparer.Ordinal),
        CustomWords = source.CustomWords
            .Select(c => new LearnedCustomWord
            {
                Word = c.Word,
                Pinyin = c.Pinyin,
                Count = c.Count,
                LastUsedUtc = c.LastUsedUtc
            })
            .ToList()
    };

    private static void ClampAndEvict(LearningData data)
    {
        foreach (var key in data.Unigram.Keys.Where(key => data.Unigram[key] < 0).ToList())
        {
            data.Unigram[key] = 0;
        }

        if (data.Unigram.Count > MaxUnigram)
        {
            foreach (var key in data.Unigram.OrderBy(pair => pair.Value).Take(data.Unigram.Count - MaxUnigram).Select(pair => pair.Key).ToList())
            {
                data.Unigram.Remove(key);
            }
        }

        var edgeCount = data.Bigram.Values.Sum(next => next.Count);
        if (edgeCount > MaxBigramEdges)
        {
            var flat = data.Bigram
                .SelectMany(outer => outer.Value.Select(inner => (outer.Key, inner.Key, inner.Value)))
                .OrderBy(triple => triple.Value)
                .Take(edgeCount - MaxBigramEdges)
                .ToList();
            foreach (var (previous, word, _) in flat)
            {
                if (data.Bigram.TryGetValue(previous, out var next))
                {
                    next.Remove(word);
                    if (next.Count == 0)
                    {
                        data.Bigram.Remove(previous);
                    }
                }
            }
        }

        if (data.CodeBoost.Count > MaxCodeBoostCodes)
        {
            foreach (var code in data.CodeBoost.Keys.Take(data.CodeBoost.Count - MaxCodeBoostCodes).ToList())
            {
                data.CodeBoost.Remove(code);
            }
        }

        if (data.CustomWords.Count > MaxCustomWords)
        {
            data.CustomWords = data.CustomWords
                .OrderBy(c => c.LastUsedUtc)
                .Take(MaxCustomWords)
                .ToList();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        SaveIfDirty();
        _gate.Dispose();
    }
}
