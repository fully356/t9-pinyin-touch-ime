using T9Pane.Services;

namespace T9Pane.Tests;

public class PinyinInputParserTests
{
    [Fact]
    public void Digits_keep_only_t9_keys()
    {
        Assert.True(PinyinInputParser.TryDigits("644-26x", out var input));
        Assert.Equal("64426", input.Code);
        Assert.Equal(SentenceInputMode.Digits, input.Mode);
        Assert.Empty(input.HardBoundaries);
    }

    [Fact]
    public void Digits_reject_input_without_any_t9_key()
    {
        Assert.False(PinyinInputParser.TryDigits("abc", out _));
        Assert.False(PinyinInputParser.TryDigits("", out _));
    }

    [Fact]
    public void Letters_record_apostrophe_as_hard_boundary_before_compacting()
    {
        Assert.True(PinyinInputParser.TryLetters("xi'an", out var input));
        Assert.Equal("xian", input.Code);
        Assert.Contains(2, input.HardBoundaries);
    }

    [Fact]
    public void Letters_fold_tones_and_split_on_space_or_dash()
    {
        Assert.True(PinyinInputParser.TryLetters("lǚ-xíng", out var input));
        Assert.Equal("lvxing", input.Code);
        Assert.Contains(2, input.HardBoundaries);
    }

    [Fact]
    public void Cache_key_distinguishes_hard_boundary()
    {
        PinyinInputParser.TryLetters("xian", out var plain);
        PinyinInputParser.TryLetters("xi'an", out var bounded);

        Assert.Equal("L:xian", PinyinInputParser.CacheKey(plain));
        Assert.NotEqual(
            PinyinInputParser.CacheKey(plain),
            PinyinInputParser.CacheKey(bounded));
    }

    [Fact]
    public void Cache_key_prefixes_mode()
    {
        PinyinInputParser.TryDigits("64", out var digits);
        PinyinInputParser.TryLetters("ni", out var letters);

        Assert.StartsWith("D:", PinyinInputParser.CacheKey(digits));
        Assert.StartsWith("L:", PinyinInputParser.CacheKey(letters));
    }
}
