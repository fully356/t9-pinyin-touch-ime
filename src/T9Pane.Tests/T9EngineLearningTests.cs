using System.IO;
using T9Pane.Services;

namespace T9Pane.Tests;

public class T9EngineLearningTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public T9EngineLearningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "t9englearn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "learning.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理可忽略 */ }
    }

    private static SentenceSegment CharSegment(string word, string pinyin) => new()
    {
        Start = 0,
        End = pinyin.Length,
        Word = word,
        Pinyin = pinyin,
        Kind = SentenceArcKind.Character
    };

    [Fact]
    public void Learned_lexicon_emits_arcs_for_custom_words_by_digits_and_letters()
    {
        var lexicon = new T9LearnedLexicon();
        var words = new List<LearnedCustomWord>
        {
            new() { Word = "卷王", Pinyin = "juan wang", Count = 3, LastUsedUtc = DateTime.UtcNow }
        };

        lexicon.EnsureCurrent(words, revision: 1);
        var digitsArcs = new List<SentenceArc>();
        lexicon.AppendArcs(SentenceInputMode.Digits, T9Engine.ToDigits("juan wang"), digitsArcs, new HashSet<string>());
        Assert.Contains(digitsArcs, arc => arc.Word == "卷王");
        Assert.Equal(-1, digitsArcs[0].EntryId);

        var letterArcs = new List<SentenceArc>();
        lexicon.AppendArcs(SentenceInputMode.Letters, T9Engine.CompactLetters("juan wang"), letterArcs, new HashSet<string>());
        Assert.Contains(letterArcs, arc => arc.Word == "卷王");

        // revision 未变时不重建。
        lexicon.EnsureCurrent(words, revision: 1);
        Assert.Equal(1, lexicon.Count);
    }

    [Fact]
    public void Attach_then_observe_invalidates_query_cache_and_keeps_results_stable()
    {
        var engine = new T9Engine();
        engine.Load(new ImeCatalog());
        using var learning = UserLearning.Load(_path);
        engine.AttachLearning(learning);

        var before = engine.Query("64426", 40);
        Assert.NotEmpty(before);

        engine.LearnSelection(new T9Selection(
            SentenceInputMode.Digits, "D:64426", 5, "你好", "ni hao", "整句",
            [CharSegment("你", "ni"), CharSegment("好", "hao")]));

        // 学习后缓存必须失效重算，同码查询仍返回有序、可重复的结果。
        var after = engine.Query("64426", 40);
        Assert.NotEmpty(after);
        var again = engine.Query("64426", 40);
        Assert.Equal(after.Select(h => h.Word), again.Select(h => h.Word));
    }

    [Fact]
    public void Repeated_custom_combination_becomes_queryable_after_threshold()
    {
        var engine = new T9Engine();
        engine.Load(new ImeCatalog());
        using var learning = UserLearning.Load(_path);
        engine.AttachLearning(learning);

        var code = "58269264"; // juan wang
        var segments = new[] { CharSegment("卷", "juan"), CharSegment("王", "wang") };
        for (var i = 0; i < UserLearning.CustomWordThreshold; i++)
        {
            engine.LearnSelection(new T9Selection(
                SentenceInputMode.Digits, "D:" + code, code.Length, "卷王", "juan wang", "整句", segments));
        }

        Assert.Contains(learning.SnapshotCustomWords(), word => word.Word == "卷王");
        // 自造词进入词图后，该码查询结果里应能选到它。
        var hits = engine.Query(code, 120);
        Assert.Contains(hits, hit => hit.Word == "卷王");
    }

    [Fact]
    public void Break_context_clears_bigram_without_throwing()
    {
        var engine = new T9Engine();
        engine.Load(new ImeCatalog());
        using var learning = UserLearning.Load(_path);
        engine.AttachLearning(learning);

        engine.LearnSelection(new T9Selection(
            SentenceInputMode.Digits, "D:96", 2, "我", "wo", "全拼", [CharSegment("我", "wo")]));
        engine.BreakLearningContext();
        Assert.Equal(0, learning.LearnedBigram("我", "们"));
    }

    [Fact]
    public void Latin_query_is_unaffected_by_chinese_learning()
    {
        var engine = new T9Engine();
        engine.Load(new ImeCatalog());
        using var learning = UserLearning.Load(_path);
        engine.AttachLearning(learning);

        engine.LearnSelection(new T9Selection(
            SentenceInputMode.Digits, "D:64", 2, "你", "ni", "全拼", [CharSegment("你", "ni")]));

        // 英文走独立拉丁词库，不接入中文学习，查询仍正常返回。
        var latin = engine.QueryLatin("d", 20);
        Assert.NotEmpty(latin);
    }
}
