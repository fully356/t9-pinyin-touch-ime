using T9Pane.Services;

namespace T9Pane.Tests;

public class CandidateFlowTests
{
    [Fact]
    public void Short_words_pack_into_five_column_rows()
    {
        var words = new[] { "你", "好", "米", "高", "泥", "搞", "干" };
        var rows = CandidateFlow.Plan(words);

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].FullWidth);
        Assert.Equal(5, rows[0].Indices.Count);
        Assert.Equal(2, rows[1].Indices.Count);
        Assert.Equal([0, 1, 2, 3, 4], rows[0].Indices);
        Assert.Equal([5, 6], rows[1].Indices);
    }

    [Fact]
    public void Long_candidate_takes_its_own_full_width_row()
    {
        var words = new[] { "你好", "我们今天去公园吧", "世界" };
        var rows = CandidateFlow.Plan(words);

        // 长句（8 字）独占一行，把前后短词批次分隔成上下两个网格行。
        Assert.Equal(3, rows.Count);
        Assert.False(rows[0].FullWidth);
        Assert.Equal([0], rows[0].Indices);
        Assert.True(rows[1].FullWidth);
        Assert.Single(rows[1].Indices);
        Assert.Equal(1, rows[1].Indices[0]);
        Assert.False(rows[2].FullWidth);
        Assert.Equal([2], rows[2].Indices);
    }

    [Fact]
    public void Pending_short_batch_flushes_before_a_long_word()
    {
        var words = new[] { "一", "二", "我们明天就出发" };
        var rows = CandidateFlow.Plan(words);

        Assert.Equal(2, rows.Count);
        Assert.Equal([0, 1], rows[0].Indices);
        Assert.True(rows[1].FullWidth);
        Assert.Equal(2, rows[1].Indices[0]);
    }

    [Fact]
    public void Consecutive_long_words_each_get_full_rows()
    {
        var words = new[] { "今天天气真好呀", "我们明天就出发" };
        var rows = CandidateFlow.Plan(words);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.FullWidth));
        Assert.Equal(0, rows[0].Indices[0]);
        Assert.Equal(1, rows[1].Indices[0]);
    }

    [Fact]
    public void Content_height_counts_full_rows_taller()
    {
        var words = new[] { "你好", "我们今天去公园吧" };
        var rows = CandidateFlow.Plan(words);
        const double cell = 38;

        var height = CandidateFlow.ContentHeight(rows, cell);
        var expected = cell + CandidateFlow.FullRowHeight(cell);
        Assert.Equal(expected, height);
        Assert.True(CandidateFlow.FullRowHeight(cell) > cell);
    }

    [Theory]
    [InlineData("你好", false)]
    [InlineData("我们今天", false)]
    [InlineData("一二三四五", true)]
    public void Long_threshold_is_five_characters(string word, bool expectedLong)
    {
        Assert.Equal(expectedLong, CandidateFlow.IsLong(word));
    }

    [Fact]
    public void Empty_input_plans_no_rows()
    {
        Assert.Empty(CandidateFlow.Plan([]));
    }
}
