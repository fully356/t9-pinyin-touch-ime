using T9Pane.Services;

namespace T9Pane.Tests;

/// <summary>
/// 阶段③：真实词库上的整句解码集成。验证长串产出多段「整句」、完整覆盖输入，
/// 短串不被无意义包装，以及排序/消费策略与现有候选合流。
/// </summary>
public class T9EngineSentenceTests : IClassFixture<T9EngineFixture>
{
    private readonly T9Engine _engine;

    public T9EngineSentenceTests(T9EngineFixture fixture) => _engine = fixture.Engine;

    [Fact]
    public void Long_digit_stream_produces_a_full_coverage_sentence_candidate()
    {
        // 我(96) 们(636) 今(546) 天(8426) 去(78) 公(4664) 园(9826)
        const string code = "9663654684267846649826";
        var hits = _engine.Query(code, 120);

        var sentences = hits.Where(hit => hit.MatchKind == "整句").ToList();
        Assert.NotEmpty(sentences);

        var top = sentences[0];
        Assert.Equal(code.Length, top.ConsumedCodeLength);
        Assert.NotNull(top.Segments);
        Assert.True(top.Segments!.Count >= 2);
        Assert.True(top.Score.HasValue);
        Assert.All(top.Segments, segment => Assert.NotEqual(SentenceArcKind.Fallback, segment.Kind));
    }

    [Fact]
    public void Long_stream_top_sentence_is_the_simplified_full_coverage_phrase()
    {
        // 我(96) 们(636) 今(546) 天(8426) 去(78) 公(4664) 园(9826)
        const string code = "9663654684267846649826";
        var sentences = _engine.Query(code, 120)
            .Where(hit => hit.MatchKind == "整句")
            .ToList();

        Assert.NotEmpty(sentences);
        // 识别出的简体多字词路径必须压过高频繁体单字拼出的路径。
        Assert.Equal("我们今天去公园", sentences[0].Word);
        Assert.Equal(code.Length, sentences[0].ConsumedCodeLength);
    }

    [Fact]
    public void Single_syllable_query_is_not_wrapped_as_a_sentence()
    {
        // 单字/单音节只有一段，应走现有全拼候选，不产生「整句」包装。
        var hits = _engine.Query("64", 120);
        Assert.DoesNotContain(hits, hit => hit.MatchKind == "整句");
    }

    [Fact]
    public void Letter_query_also_emits_sentence_candidates_for_long_input()
    {
        // women（我们）字母全拼，跨两个音节且非单一整词时应能组句。
        var hits = _engine.QueryLetters("womenjintian", 120);
        var sentences = hits.Where(hit => hit.MatchKind == "整句").ToList();
        Assert.NotEmpty(sentences);
        Assert.All(sentences, hit => Assert.True(hit.ConsumedCodeLength > 0));
    }

    [Fact]
    public void Sentence_candidate_survives_ranking_and_appears_in_returned_take()
    {
        const string code = "9663654684267846649826";
        var wide = _engine.Query(code, 120);
        var narrow = _engine.Query(code, 16);

        // 缓存切片一致性：小 take 是大 take 的前缀。
        Assert.Equal(
            wide.Take(narrow.Count).Select(hit => hit.Word),
            narrow.Select(hit => hit.Word));
    }

    [Fact]
    public void Sentence_kind_ranks_above_jianpin_but_below_exact_quanpin()
    {
        var rank = T9MatchRank.Kind;
        Assert.True(rank("全拼") > rank("整句"));
        Assert.True(rank("整句") == rank("组句"));
        Assert.True(rank("整句") > rank("简拼"));
    }

    [Fact]
    public void Composition_policy_consumes_the_whole_input_for_a_sentence()
    {
        const string typed = "9663654684267846649826";
        var consumed = CompositionConsumePolicy.ConsumeDigits("整句", "wo men jin tian", typed);
        Assert.Equal(typed.Length, consumed);
        Assert.False(CompositionConsumePolicy.HasRemainder(consumed, typed.Length));
    }
}
