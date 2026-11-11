using System.IO;
using T9Pane.Services;

namespace T9Pane.Tests;

public class UserLearningTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public UserLearningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "t9learning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "learning.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 测试清理失败可忽略 */ }
    }

    private static SentenceSegment Segment(string word, string pinyin) => new()
    {
        Start = 0,
        End = pinyin.Length,
        Word = word,
        Pinyin = pinyin,
        Kind = SentenceArcKind.Character
    };

    private T9Selection Selection(string code, string word, string pinyin, IReadOnlyList<SentenceSegment> segments) =>
        new(SentenceInputMode.Digits, code, code.Length, word, pinyin, "整句", segments);

    [Fact]
    public void Missing_file_loads_as_empty_without_throwing()
    {
        using var learning = UserLearning.Load(_path);
        Assert.Equal(0, learning.TotalLearned);
        Assert.Empty(learning.SnapshotCustomWords());
    }

    [Fact]
    public void Unigram_and_bigram_round_trip_through_disk()
    {
        using (var learning = UserLearning.Load(_path))
        {
            learning.Observe(Selection("96636", "我们", "wo men",
                [Segment("我", "wo"), Segment("们", "men")]));
            learning.Flush();
            Assert.Equal(1, learning.LearnedUnigram("我"));
            Assert.Equal(1, learning.LearnedUnigram("们"));
            Assert.Equal(1, learning.LearnedBigram("我", "们"));
        }

        using var reloaded = UserLearning.Load(_path);
        Assert.Equal(1, reloaded.LearnedUnigram("我"));
        Assert.Equal(1, reloaded.LearnedBigram("我", "们"));
    }

    [Fact]
    public void Chained_segments_build_bigram_chain()
    {
        using var learning = UserLearning.Load(_path);
        learning.Observe(Selection("96636546", "我们今天", "wo men jin tian",
            [Segment("我", "wo"), Segment("们", "men"), Segment("今", "jin"), Segment("天", "tian")]));

        Assert.Equal(1, learning.LearnedBigram("我", "们"));
        Assert.Equal(1, learning.LearnedBigram("们", "今"));
        Assert.Equal(1, learning.LearnedBigram("今", "天"));
        Assert.Equal(0, learning.LearnedBigram("我", "今"));
    }

    [Fact]
    public void Break_context_prevents_bigram_across_non_chinese_input()
    {
        using var learning = UserLearning.Load(_path);
        learning.Observe(Selection("96", "我", "wo", [Segment("我", "wo")]));
        learning.BreakContext();
        learning.Observe(Selection("636", "们", "men", [Segment("们", "men")]));

        Assert.Equal(0, learning.LearnedBigram("我", "们"));
    }

    [Fact]
    public void Digit_and_letter_codes_do_not_collide()
    {
        using var learning = UserLearning.Load(_path);
        learning.Observe(new T9Selection(SentenceInputMode.Digits, "D:96", 2, "我", "wo", "整句",
            [Segment("我", "wo")]));
        learning.Observe(new T9Selection(SentenceInputMode.Letters, "L:wo", 2, "握", "wo", "整句",
            [Segment("握", "wo")]));
        learning.Flush();

        var digit = learning.RememberedSelections("D:96");
        var letter = learning.RememberedSelections("L:wo");
        Assert.Equal("我", digit.Single().Word);
        Assert.Equal("握", letter.Single().Word);
    }

    [Fact]
    public void Corrupt_json_falls_back_to_backup_then_empty()
    {
        File.WriteAllText(_path, "{ 这不是合法 JSON");
        using var learning = UserLearning.Load(_path);
        Assert.Equal(0, learning.TotalLearned);
    }

    [Fact]
    public void Backup_recovers_when_primary_is_corrupt()
    {
        using (var first = UserLearning.Load(_path))
        {
            first.Observe(Selection("64", "你", "ni", [Segment("你", "ni")]));
            first.Flush();
        }

        Assert.True(File.Exists(_path + ".bak"));
        File.WriteAllText(_path, "corrupt");
        using var reloaded = UserLearning.Load(_path);
        Assert.Equal(1, reloaded.LearnedUnigram("你"));
    }

    [Fact]
    public void Repeated_character_combo_becomes_custom_word_after_threshold()
    {
        using var learning = UserLearning.Load(_path);
        var segments = new[] { Segment("卷", "juan"), Segment("王", "wang") };
        for (var i = 0; i < UserLearning.CustomWordThreshold; i++)
        {
            learning.Observe(Selection("58269264", "卷王", "juan wang", segments));
        }

        var custom = learning.SnapshotCustomWords();
        Assert.Contains(custom, word => word.Word == "卷王" && word.Pinyin == "juan wang");
    }

    [Fact]
    public void Single_selection_does_not_create_custom_word()
    {
        using var learning = UserLearning.Load(_path);
        learning.Observe(Selection("58269264", "卷王", "juan wang",
            [Segment("卷", "juan"), Segment("王", "wang")]));
        Assert.DoesNotContain(learning.SnapshotCustomWords(), word => word.Word == "卷王");
    }

    [Fact]
    public void Revision_increments_on_observation()
    {
        using var learning = UserLearning.Load(_path);
        var before = learning.Revision;
        learning.Observe(Selection("64", "你", "ni", [Segment("你", "ni")]));
        Assert.True(learning.Revision > before);
    }
}
