using T9Pane.Services;

namespace T9Pane.Tests;

public class SentenceScoreModelTests
{
    private static SentenceArc Word(string word, int frequency = 100, SentenceArcKind kind = SentenceArcKind.Word) =>
        new(2, word, "ni", frequency, kind, 0);

    [Fact]
    public void Zero_frequency_and_zero_total_still_yield_finite_score()
    {
        var model = new SentenceScoreModel(totalBaseFrequency: 0, vocabularySize: 10);
        var score = model.Score(Word("的", 0), null);
        Assert.False(double.IsNaN(score));
        Assert.False(double.IsInfinity(score));
    }

    [Fact]
    public void Higher_base_frequency_scores_higher()
    {
        var model = new SentenceScoreModel(totalBaseFrequency: 1_000_000, vocabularySize: 1000);
        var high = model.Score(Word("的", 900_000), null);
        var low = model.Score(Word("地", 100), null);
        Assert.True(high > low);
    }

    [Fact]
    public void Learned_unigram_lifts_a_repeatedly_chosen_word()
    {
        var learning = new FakeLearningView { TotalLearned = 500 };
        learning.Unigram["你好"] = 200;
        var model = new SentenceScoreModel(1_000_000, 1000, learning);

        var withLearning = model.Score(Word("你好", 10), null);
        var plain = new SentenceScoreModel(1_000_000, 1000).Score(Word("你好", 10), null);

        Assert.True(withLearning > plain);
    }

    [Fact]
    public void Unseen_bigram_falls_back_to_unigram_level()
    {
        var learning = new FakeLearningView { TotalLearned = 100 };
        var model = new SentenceScoreModel(1_000_000, 1000, learning);

        // 没有任何 bigram 观测，给定上文不应产生异常，分数仍有限。
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
        var model = new SentenceScoreModel(1_000_000, 1000, learning);

        var withContext = model.Score(Word("北京", 100), "去");
        var noContext = model.Score(Word("北京", 100), null);
        Assert.True(withContext > noContext);
    }

    [Fact]
    public void Multi_character_word_gets_length_prior_but_single_char_does_not()
    {
        var model = new SentenceScoreModel(1_000_000, 1000);
        var single = new SentenceArc(2, "好", "hao", 100, SentenceArcKind.Word, 0);
        var phrase = new SentenceArc(6, "你好呀", "ni hao ya", 100, SentenceArcKind.Word, 0);

        var singleScore = model.Score(single, null);
        var phraseScore = model.Score(phrase, null);
        // 同样词频下，更长的词拿到词长先验，分数更高（更倾向成词而非逐字）。
        Assert.True(phraseScore > singleScore);
    }

    [Fact]
    public void Prefix_and_fallback_arc_kinds_are_penalized()
    {
        var model = new SentenceScoreModel(1_000_000, 1000);
        var normal = model.Score(new SentenceArc(2, "好", "hao", 100, SentenceArcKind.Word, 0), null);
        var prefix = model.Score(new SentenceArc(2, "好", "hao", 100, SentenceArcKind.Prefix, 0), null);
        var fallback = model.Score(new SentenceArc(1, "4", "", 0, SentenceArcKind.Fallback, 0), null);

        Assert.True(normal > prefix);
        Assert.True(prefix > fallback);
    }

    [Fact]
    public void Huge_learning_counts_do_not_overflow_or_produce_nan()
    {
        var learning = new FakeLearningView { TotalLearned = int.MaxValue };
        learning.Unigram["的"] = int.MaxValue;
        learning.Outgoing["我"] = int.MaxValue;
        learning.Bigram[("我", "的")] = int.MaxValue;
        var model = new SentenceScoreModel(int.MaxValue, int.MaxValue, learning);

        var score = model.Score(Word("的", int.MaxValue), "我");
        Assert.False(double.IsNaN(score));
        Assert.False(double.IsInfinity(score));
    }
}
