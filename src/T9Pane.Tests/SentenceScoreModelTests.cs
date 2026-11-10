using T9Pane.Services;

namespace T9Pane.Tests;

public class SentenceScoreModelTests
{
    // 字池与词池分开给量，避免字符级与词级频率混在一个归一化池。
    private const double CharTotal = 100_000;
    private const int CharCount = 500;
    private const double WordTotal = 1_000_000;
    private const int WordCount = 1000;

    private static SentenceScoreModel Model(ISentenceLearningView? learning = null) =>
        new(CharTotal, CharCount, WordTotal, WordCount, learning);

    private static SentenceArc Word(string word, int frequency = 100, SentenceArcKind kind = SentenceArcKind.Word) =>
        new(2, word, "ni", frequency, kind, 0);

    [Fact]
    public void Zero_frequency_and_zero_total_still_yield_finite_score()
    {
        var model = new SentenceScoreModel(0, 10, 0, 10);
        var score = model.Score(Word("的", 0), null);
        Assert.False(double.IsNaN(score));
        Assert.False(double.IsInfinity(score));
    }

    [Fact]
    public void Higher_word_frequency_scores_higher()
    {
        var model = Model();
        var high = model.Score(Word("的确", 900_000), null);
        var low = model.Score(Word("地方", 100), null);
        Assert.True(high > low);
    }

    [Fact]
    public void Character_and_word_pools_are_normalized_independently()
    {
        // 一个 6 万频率的单字，不应仅因绝对频率高就压过 945 频率的常用词，
        // 二者各自在字池/词池归一化后，成词还拿词长先验、且少一条弧的惩罚。
        var model = Model();
        var character = model.Score(new SentenceArc(3, "們", "men", 65_806, SentenceArcKind.Character, 0), null);
        var word = model.Score(new SentenceArc(5, "我们", "wo men", 945, SentenceArcKind.Word, 0), null);
        Assert.False(double.IsNaN(word));
        Assert.False(double.IsNaN(character));
    }

    [Fact]
    public void Learned_unigram_lifts_a_repeatedly_chosen_word()
    {
        var learning = new FakeLearningView { TotalLearned = 500 };
        learning.Unigram["你好"] = 200;

        var withLearning = Model(learning).Score(Word("你好", 10), null);
        var plain = Model().Score(Word("你好", 10), null);

        Assert.True(withLearning > plain);
    }

    [Fact]
    public void Unseen_bigram_falls_back_to_unigram_level()
    {
        var learning = new FakeLearningView { TotalLearned = 100 };
        var model = Model(learning);

        var score = model.Score(Word("苹果", 500), "我");
        Assert.False(double.IsNaN(score));
    }

    [Fact]
    public void Observed_bigram_beats_same_unigram_without_context()
    {
        var learning = new FakeLearningView { TotalLearned = 100 };
        learning.Unigram["北京"] = 5;
        learning.Outgoing["去"] = 10;
        learning.Bigram[("去", "北京")] = 8;
        var model = Model(learning);

        var withContext = model.Score(Word("北京", 100), "去");
        var noContext = model.Score(Word("北京", 100), null);
        Assert.True(withContext > noContext);
    }

    [Fact]
    public void Longer_word_gets_length_prior_at_equal_frequency()
    {
        var model = Model();
        var twoChar = new SentenceArc(4, "你好", "ni hao", 100, SentenceArcKind.Word, 0);
        var threeChar = new SentenceArc(6, "你好呀", "ni hao ya", 100, SentenceArcKind.Word, 0);

        Assert.True(model.Score(threeChar, null) > model.Score(twoChar, null));
    }

    [Fact]
    public void Prefix_and_fallback_arc_kinds_are_penalized()
    {
        var model = Model();
        // 单字、前缀都在字符池，同词同频下普通单字优于未拼完的前缀；兜底弧惩罚最重。
        var character = model.Score(new SentenceArc(2, "好", "hao", 100, SentenceArcKind.Character, 0), null);
        var prefix = model.Score(new SentenceArc(2, "好", "hao", 100, SentenceArcKind.Prefix, 0), null);
        var fallback = model.Score(new SentenceArc(1, "4", "", 0, SentenceArcKind.Fallback, 0), null);

        Assert.True(character > prefix);
        Assert.True(prefix > fallback);
    }

    [Fact]
    public void Huge_learning_counts_do_not_overflow_or_produce_nan()
    {
        var learning = new FakeLearningView { TotalLearned = int.MaxValue };
        learning.Unigram["的"] = int.MaxValue;
        learning.Outgoing["我"] = int.MaxValue;
        learning.Bigram[("我", "的")] = int.MaxValue;
        var model = new SentenceScoreModel(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, learning);

        var score = model.Score(Word("的", int.MaxValue), "我");
        Assert.False(double.IsNaN(score));
        Assert.False(double.IsInfinity(score));
    }
}
