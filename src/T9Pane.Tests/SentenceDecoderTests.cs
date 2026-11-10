using T9Pane.Services;

namespace T9Pane.Tests;

public class SentenceDecoderTests
{
    private static SentenceDecoder Decoder(SentenceDecoderOptions? options = null, TimeProvider? clock = null) =>
        new(options, clock);

    [Fact]
    public void Prefers_globally_better_segmentation_over_greedy_longest_word()
    {
        // 输入 "64426"：贪心最长词会先取一个长弧，但两字路径整体分数更高。
        var input = SentenceTestInputs.Digits("64426");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] = [SentenceTestInputs.Arc(5, "霓好", "ni hao", frequency: 1)],
            [1] = [],
            [2] = [],
            [3] = [],
            [4] = []
        };
        // 单字弧：你(64) 在 0，好(426) 在 2。
        arcs[0].Add(SentenceTestInputs.Arc(2, "你", "ni", SentenceArcKind.Character, 900));
        arcs[2].Add(SentenceTestInputs.Arc(3, "好", "hao", SentenceArcKind.Character, 900));

        var scorer = new ScriptedScorer(new Dictionary<string, double>
        {
            ["你"] = -1, ["好"] = -1, ["霓好"] = -20
        });

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer);

        Assert.Equal("你好", result.Paths[0].Word);
        Assert.Equal(2, result.Paths[0].Segments.Count);
    }

    [Fact]
    public void Xian_offers_both_single_syllable_and_split_paths()
    {
        var input = SentenceTestInputs.Letters("xian");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] =
            [
                SentenceTestInputs.Arc(4, "先", "xian", SentenceArcKind.Character, 500),
                SentenceTestInputs.Arc(2, "西", "xi", SentenceArcKind.Character, 500)
            ],
            [2] = [SentenceTestInputs.Arc(2, "安", "an", SentenceArcKind.Character, 500)]
        };

        var scorer = new ScriptedScorer(new Dictionary<string, double>
        {
            ["先"] = -2, ["西"] = -2, ["安"] = -2
        });

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer, take: 8);
        var words = result.Paths.Select(p => p.Word).ToList();

        Assert.Contains("先", words);
        Assert.Contains("西安", words);
    }

    [Fact]
    public void Hard_boundary_forbids_arcs_crossing_it()
    {
        var input = SentenceTestInputs.Letters("xian", new HashSet<int> { 2 });
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] =
            [
                SentenceTestInputs.Arc(4, "先", "xian", SentenceArcKind.Character, 9999),
                SentenceTestInputs.Arc(2, "西", "xi", SentenceArcKind.Character, 100)
            ],
            [2] = [SentenceTestInputs.Arc(2, "安", "an", SentenceArcKind.Character, 100)]
        };
        var scorer = new ScriptedScorer(new Dictionary<string, double>
        {
            ["先"] = 0, ["西"] = -5, ["安"] = -5
        });

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer);

        // 即便"先"分数高得多，硬边界禁止它跨位置 2，首选必须是西安。
        Assert.Equal("西安", result.Paths[0].Word);
        Assert.DoesNotContain(result.Paths, p => p.Word == "先");
    }

    [Fact]
    public void Top_n_is_score_ordered_and_deduplicated_by_word()
    {
        var input = SentenceTestInputs.Digits("64");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] =
            [
                SentenceTestInputs.Arc(2, "你", "ni", SentenceArcKind.Character, 100),
                SentenceTestInputs.Arc(2, "泥", "ni", SentenceArcKind.Character, 50)
            ]
        };
        var scorer = new ScriptedScorer(new Dictionary<string, double>
        {
            ["你"] = -1, ["泥"] = -3
        });

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer, take: 5);

        // 真实候选按分数排序在前；解码器另外保底的数字兜底路径排在最后。
        Assert.Equal(["你", "泥"], result.Paths.Select(p => p.Word).Take(2));
        Assert.True(result.Paths[0].Score > result.Paths[1].Score);
        Assert.Equal("64", result.Paths[^1].Word);
    }

    [Fact]
    public void Falls_back_character_by_character_when_no_word_arcs()
    {
        var input = SentenceTestInputs.Digits("234");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] = [SentenceTestInputs.Arc(1, "啊", "a", SentenceArcKind.Character, 100)],
            [1] = [SentenceTestInputs.Arc(1, "的", "de", SentenceArcKind.Character, 100)],
            [2] = [SentenceTestInputs.Arc(1, "个", "ge", SentenceArcKind.Character, 100)]
        };
        var scorer = new ScriptedScorer();

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer);

        Assert.Equal("啊的个", result.Paths[0].Word);
        Assert.False(result.Paths[0].HasFallback);
        Assert.Equal(3, result.Paths[0].ConsumedCodeLength);
    }

    [Fact]
    public void Always_returns_full_coverage_path_via_single_key_fallback()
    {
        var input = SentenceTestInputs.Digits("299");
        // 完全没有真实弧。
        var arcs = new Dictionary<int, List<SentenceArc>>();
        var result = Decoder().Decode(new FakeArcProvider(input, arcs), new ScriptedScorer());

        var top = result.Paths[0];
        Assert.Equal(3, top.ConsumedCodeLength);
        Assert.True(top.HasFallback);
        Assert.Equal("299", top.Word);
        Assert.All(top.Segments, s => Assert.Equal(SentenceArcKind.Fallback, s.Kind));
    }

    [Fact]
    public void Fills_a_middle_gap_with_fallback_but_keeps_real_words()
    {
        var input = SentenceTestInputs.Digits("64299");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] = [SentenceTestInputs.Arc(2, "你", "ni", SentenceArcKind.Character, 100)]
            // 位置 2..4 无任何真实弧，靠兜底补齐。
        };
        var scorer = new ScriptedScorer(new Dictionary<string, double> { ["你"] = -1 });

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer);

        var top = result.Paths[0];
        Assert.Equal(5, top.ConsumedCodeLength);
        Assert.StartsWith("你", top.Word);
        Assert.True(top.HasFallback);
    }

    [Fact]
    public void Prefix_arc_only_at_the_tail_consumes_typed_length_not_completed_pinyin()
    {
        var input = SentenceTestInputs.Letters("niha");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] = [SentenceTestInputs.Arc(2, "你", "ni", SentenceArcKind.Character, 100)],
            [2] = [SentenceTestInputs.Arc(2, "好", "hao", SentenceArcKind.Prefix, 100)] // 只打了 ha
        };
        var result = Decoder().Decode(new FakeArcProvider(input, arcs), new ScriptedScorer());

        var top = result.Paths[0];
        Assert.Equal("你好", top.Word);
        Assert.Equal(4, top.ConsumedCodeLength);
        Assert.True(top.HasPrefix);
        Assert.Equal("hao", top.Segments[1].Pinyin);
    }

    [Fact]
    public void Bigram_context_reorders_candidates_using_previous_word()
    {
        var input = SentenceTestInputs.Digits("96");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] =
            [
                SentenceTestInputs.Arc(2, "我", "wo", SentenceArcKind.Character, 100),
                SentenceTestInputs.Arc(2, "握", "wo", SentenceArcKind.Character, 100)
            ]
        };
        // 单看 unigram 二者同分；上文"你"强烈倾向"我"。
        var scorer = new ScriptedScorer(
            new Dictionary<string, double> { ["我"] = -2, ["握"] = -2 },
            new Dictionary<(string, string), double> { [("你", "我")] = 5 });

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), scorer, previousWord: "你");

        Assert.Equal("我", result.Paths[0].Word);
    }

    [Fact]
    public void Confirmed_syllables_prune_non_matching_paths_inside_dp()
    {
        var input = SentenceTestInputs.Digits("64426");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] =
            [
                SentenceTestInputs.Arc(2, "你", "ni", SentenceArcKind.Character, 100),
                SentenceTestInputs.Arc(2, "米", "mi", SentenceArcKind.Character, 100)
            ],
            [2] = [SentenceTestInputs.Arc(3, "好", "hao", SentenceArcKind.Character, 100)]
        };
        // 让"米好"分数更高，确认音节 ni 应把它剪掉。
        var scorer = new ScriptedScorer(new Dictionary<string, double>
        {
            ["你"] = -5, ["米"] = 0, ["好"] = -1
        });

        var result = Decoder().Decode(
            new FakeArcProvider(input, arcs), scorer,
            confirmedSyllables: ["ni"]);

        // 确认音节 ni 把「米」路径在 DP 内剪掉，正确的「你好」成为首选；
        // 确认音节满足之后仍可能存在低分的兜底混合路径，但不得含被剪的「米」。
        Assert.Equal("你好", result.Paths[0].Word);
        Assert.DoesNotContain(result.Paths, p => p.Word.Contains("米"));
    }

    [Fact]
    public void Defensive_ignores_zero_length_and_out_of_range_arcs()
    {
        var input = SentenceTestInputs.Digits("64");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] =
            [
                new SentenceArc(0, "空", "", 1, SentenceArcKind.Word, 0),
                new SentenceArc(9, "越界", "yue jie", 1, SentenceArcKind.Word, 0),
                SentenceTestInputs.Arc(2, "你", "ni", SentenceArcKind.Character, 100)
            ]
        };

        var result = Decoder().Decode(new FakeArcProvider(input, arcs), new ScriptedScorer());

        Assert.Equal("你", result.Paths[0].Word);
    }

    [Fact]
    public void Empty_or_oversized_input_returns_no_paths()
    {
        var empty = SentenceTestInputs.Digits("");
        Assert.Empty(Decoder().Decode(new FakeArcProvider(empty, []), new ScriptedScorer()).Paths);

        var longInput = SentenceTestInputs.Digits(new string('9', 70));
        var result = Decoder(new SentenceDecoderOptions { MaxInputLength = 64 })
            .Decode(new FakeArcProvider(longInput, []), new ScriptedScorer());
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void Timeout_still_keeps_a_full_coverage_fallback_path()
    {
        var clock = new ManualTimeProvider();
        // 预算只给 1ms；评分循环里推进时钟强制超时。
        var options = new SentenceDecoderOptions
        {
            TimeBudgetMilliseconds = 1,
            MaxTransitions = 1_000_000
        };
        var input = SentenceTestInputs.Digits("345");
        var arcs = new Dictionary<int, List<SentenceArc>>
        {
            [0] = ManyArcs(0),
            [1] = ManyArcs(1),
            [2] = ManyArcs(2)
        };

        var decoder = Decoder(options, clock);
        var result = decoder.Decode(
            new TimingFakeProvider(input, arcs, clock),
            new ScriptedScorer());

        Assert.True(result.TimedOut);
        Assert.Equal(3, result.Paths[0].ConsumedCodeLength);
    }

    private static List<SentenceArc> ManyArcs(int start) =>
        Enumerable.Range(0, 40)
            .Select(i => SentenceTestInputs.Arc(1, "字" + start + i, "zi", SentenceArcKind.Character, 1))
            .ToList();

    private sealed class TimingFakeProvider : ISentenceArcProvider
    {
        private readonly FakeArcProvider _inner;
        private readonly ManualTimeProvider _clock;

        public TimingFakeProvider(SentenceInput input, Dictionary<int, List<SentenceArc>> arcs, ManualTimeProvider clock)
        {
            _inner = new FakeArcProvider(input, arcs);
            _clock = clock;
        }

        public SentenceInput Input => _inner.Input;

        public void AppendArcs(int start, List<SentenceArc> destination)
        {
            _inner.AppendArcs(start, destination);
            _clock.AdvanceMilliseconds(5); // 每次建弧后超时
        }
    }
}
