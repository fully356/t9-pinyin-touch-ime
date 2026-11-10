namespace T9Pane.Services;

/// <summary>
/// 评分阶段只读的用户学习视图。阶段④的 UserLearning 会实现它；
/// 没有学习数据时传 null，模型退化为纯内置词频 unigram。
/// </summary>
internal interface ISentenceLearningView
{
    int TotalLearned { get; }
    int LearnedUnigram(string word);
    int LearnedBigram(string previous, string word);
    int LearnedOutgoing(string previous);
}

internal sealed record SentenceScoreOptions
{
    public double UnigramSmoothing { get; init; } = 0.1;
    public double UserUnigramWeightCeiling { get; init; } = 0.35;
    public double UserUnigramHalfLife { get; init; } = 200;
    public double BigramBackoffDelta { get; init; } = 8;
    public double BigramWeight { get; init; } = 0.45;
    public double PerArcPenalty { get; init; } = 0.30;
    public double WordLengthPriorStep { get; init; } = 0.12;
    public int WordLengthPriorCap { get; init; } = 3;
    public double PrefixPenalty { get; init; } = -0.8;
    public double FallbackPenalty { get; init; } = -16;
}

/// <summary>
/// 弧打分：内置词频 unigram，与用户 unigram/bigram 做线性插值（含 backoff），
/// 再叠加词数、词长、弧类型先验。全部取对数相加，便于 Viterbi 累加。
/// </summary>
internal sealed class SentenceScoreModel : ISentenceScoreModel
{
    private readonly double _totalBaseFrequency;
    private readonly int _vocabularySize;
    private readonly ISentenceLearningView? _learning;
    private readonly SentenceScoreOptions _options;

    public SentenceScoreModel(
        double totalBaseFrequency,
        int vocabularySize,
        ISentenceLearningView? learning = null,
        SentenceScoreOptions? options = null)
    {
        _totalBaseFrequency = Math.Max(0, totalBaseFrequency);
        _vocabularySize = Math.Max(1, vocabularySize);
        _learning = learning;
        _options = options ?? new SentenceScoreOptions();
    }

    public double Score(in SentenceArc arc, string? previousWord)
    {
        var p1 = UnigramProbability(arc.Word, Math.Max(0, arc.Frequency));
        var probability = p1;

        if (previousWord is not null && previousWord.Length > 0 && _learning is not null)
        {
            var outgoing = _learning.LearnedOutgoing(previousWord);
            var bigram = _learning.LearnedBigram(previousWord, arc.Word);
            // 没观察过该 bigram 时，分子退化为 δ·P1，整体回落到 unigram 量级。
            var p2 = (bigram + _options.BigramBackoffDelta * p1)
                / (outgoing + _options.BigramBackoffDelta);
            probability = (1 - _options.BigramWeight) * p1 + _options.BigramWeight * p2;
        }

        var score = Math.Log(Math.Max(probability, 1e-300))
            - _options.PerArcPenalty
            + WordLengthPrior(arc)
            + KindPenalty(arc.Kind);
        return score;
    }

    private double UnigramProbability(string word, int frequency)
    {
        var eta = _options.UnigramSmoothing;
        var denominator = _totalBaseFrequency + eta * _vocabularySize;
        var baseProbability = (frequency + eta) / Math.Max(denominator, 1e-12);

        var learning = _learning;
        if (learning is null || learning.TotalLearned <= 0)
        {
            return baseProbability;
        }

        var u = learning.TotalLearned;
        var lambda = _options.UserUnigramWeightCeiling * u
            / (u + _options.UserUnigramHalfLife);
        var userProbability = (double)Math.Max(0, learning.LearnedUnigram(word)) / u;
        return (1 - lambda) * baseProbability + lambda * userProbability;
    }

    private double WordLengthPrior(in SentenceArc arc)
    {
        if (arc.Kind != SentenceArcKind.Word)
        {
            return 0;
        }

        var extra = Math.Min(Math.Max(arc.Word.Length - 1, 0), _options.WordLengthPriorCap);
        return _options.WordLengthPriorStep * extra;
    }

    private double KindPenalty(SentenceArcKind kind) => kind switch
    {
        SentenceArcKind.Prefix => _options.PrefixPenalty,
        SentenceArcKind.Fallback => _options.FallbackPenalty,
        _ => 0
    };
}
