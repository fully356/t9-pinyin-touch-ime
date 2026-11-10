using T9Pane.Services;

namespace T9Pane.Tests;

/// <summary>按位置返回预置弧，不读词库/文件，供解码器纯算法测试。</summary>
internal sealed class FakeArcProvider : ISentenceArcProvider
{
    private readonly Dictionary<int, List<SentenceArc>> _arcs;

    public FakeArcProvider(SentenceInput input, Dictionary<int, List<SentenceArc>> arcs)
    {
        Input = input;
        _arcs = arcs;
    }

    public SentenceInput Input { get; }

    public void AppendArcs(int start, List<SentenceArc> destination)
    {
        if (_arcs.TryGetValue(start, out var list))
        {
            destination.AddRange(list);
        }
    }
}

/// <summary>按词查单组分、按（上文,词）查 bigram 加分的确定性评分器。</summary>
internal sealed class ScriptedScorer : ISentenceScoreModel
{
    private readonly Dictionary<string, double> _unigram;
    private readonly Dictionary<(string Previous, string Word), double> _bigram;
    private readonly double _defaultScore;

    public ScriptedScorer(
        Dictionary<string, double>? unigram = null,
        Dictionary<(string, string), double>? bigram = null,
        double defaultScore = -10)
    {
        _unigram = unigram ?? [];
        _bigram = bigram ?? [];
        _defaultScore = defaultScore;
    }

    public double Score(in SentenceArc arc, string? previousWord)
    {
        var score = _unigram.TryGetValue(arc.Word, out var value) ? value : _defaultScore;
        if (previousWord is not null
            && _bigram.TryGetValue((previousWord, arc.Word), out var bonus))
        {
            score += bonus;
        }

        return score;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void AdvanceMilliseconds(double ms) =>
        _ticks += (long)(ms * TimeSpan.TicksPerSecond / 1000.0);
}

internal sealed class FakeLearningView : ISentenceLearningView
{
    public int TotalLearned { get; init; }
    public Dictionary<string, int> Unigram { get; } = [];
    public Dictionary<(string, string), int> Bigram { get; } = [];
    public Dictionary<string, int> Outgoing { get; } = [];

    public int LearnedUnigram(string word) => Unigram.GetValueOrDefault(word);
    public int LearnedBigram(string previous, string word) => Bigram.GetValueOrDefault((previous, word));
    public int LearnedOutgoing(string previous) => Outgoing.GetValueOrDefault(previous);
}

internal static class SentenceTestInputs
{
    public static SentenceInput Digits(string code) => new()
    {
        Code = code,
        Mode = SentenceInputMode.Digits
    };

    public static SentenceInput Letters(string code, IReadOnlySet<int>? boundaries = null) => new()
    {
        Code = code,
        Mode = SentenceInputMode.Letters,
        HardBoundaries = boundaries ?? SentenceInput.EmptySet
    };

    public static SentenceArc Arc(
        int length, string word, string pinyin,
        SentenceArcKind kind = SentenceArcKind.Word, int frequency = 100) =>
        new(length, word, pinyin, frequency, kind, 0);
}
