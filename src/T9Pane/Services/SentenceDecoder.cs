namespace T9Pane.Services;

internal enum SentenceInputMode
{
    Digits,
    Letters
}

internal enum SentenceArcKind
{
    Word,
    Character,
    Prefix,
    Fallback
}

/// <summary>一次解码输入：规范化后的码、模式和用户显式给出的硬边界（隔音符位置）。</summary>
internal sealed class SentenceInput
{
    public required string Code { get; init; }
    public required SentenceInputMode Mode { get; init; }
    public IReadOnlySet<int> HardBoundaries { get; init; } = EmptySet;

    internal static readonly IReadOnlySet<int> EmptySet = new HashSet<int>();
}

/// <summary>词图上的一条弧：从某位置消费 Length 个码，落成一个词/字。</summary>
internal readonly record struct SentenceArc(
    int Length,
    string Word,
    string Pinyin,
    int Frequency,
    SentenceArcKind Kind,
    int EntryId)
{
    public bool IsFallback => Kind == SentenceArcKind.Fallback;
}

internal sealed class SentenceSegment
{
    public required int Start { get; init; }
    public required int End { get; init; }
    public required string Word { get; init; }
    public required string Pinyin { get; init; }
    public required SentenceArcKind Kind { get; init; }
    public int Frequency { get; init; }
}

internal sealed class SentencePath
{
    public required string Word { get; init; }
    public required string Pinyin { get; init; }
    public required double Score { get; init; }
    public required int ConsumedCodeLength { get; init; }
    public required IReadOnlyList<SentenceSegment> Segments { get; init; }
    public bool HasFallback { get; init; }
    public bool HasPrefix { get; init; }
}

internal sealed record SentenceDecodeResult(
    IReadOnlyList<SentencePath> Paths,
    bool TimedOut,
    int ExpandedTransitions,
    int TotalArcs);

/// <summary>由引擎用现有词库索引实现：给出在 start 位置出发的所有真实词/字/前缀弧。</summary>
internal interface ISentenceArcProvider
{
    SentenceInput Input { get; }
    void AppendArcs(int start, List<SentenceArc> destination);
}

/// <summary>弧打分（unigram/bigram/先验），与词图解耦便于单测。</summary>
internal interface ISentenceScoreModel
{
    double Score(in SentenceArc arc, string? previousWord);
}

internal sealed record SentenceDecoderOptions
{
    public int MaxInputLength { get; init; } = 64;
    public int MaxArcSpan { get; init; } = 24;
    public int MaxWordArcsPerSpan { get; init; } = 24;
    public int MaxCharacterArcsPerSpan { get; init; } = 16;
    public int MaxPrefixArcsPerSuffix { get; init; } = 8;
    public int MaxArcsPerStart { get; init; } = 64;
    public int MaxTotalArcs { get; init; } = 4096;
    public int MaxStatesPerPosition { get; init; } = 40;
    public int MaxHypothesesPerPosition { get; init; } = 64;
    public int MaxTransitions { get; init; } = 200_000;
    public double TimeBudgetMilliseconds { get; init; } = 8;
    public int PathCount { get; init; } = 8;
}

/// <summary>
/// 在词图上做 k-best Viterbi。状态是（末词, 已确认音节进度），每个位置按状态保留分数最高的若干条路径，
/// 链表回溯。解码器自补单键兜底弧，保证任意输入都有一条完整覆盖路径。
/// </summary>
internal sealed class SentenceDecoder
{
    private readonly SentenceDecoderOptions _options;
    private readonly TimeProvider _clock;

    public SentenceDecoder(SentenceDecoderOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new SentenceDecoderOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public SentenceDecodeResult Decode(
        ISentenceArcProvider provider,
        ISentenceScoreModel scorer,
        string? previousWord = null,
        IReadOnlyList<string>? confirmedSyllables = null,
        int? take = null)
    {
        var input = provider.Input;
        var n = input.Code.Length;
        var pathCount = Math.Max(1, take ?? _options.PathCount);
        if (n == 0 || n > _options.MaxInputLength)
        {
            return new SentenceDecodeResult([], false, 0, 0);
        }

        var timestamp = _clock.GetTimestamp();
        var timedOut = false;
        var transitions = 0;
        var totalArcs = 0;

        // best[i]：到达位置 i 的假设，按状态（末词, 确认进度）归并，每状态保留 top-K。
        var best = new Dictionary<(string? LastWord, int Progress), List<SentenceHypothesis>>[n + 1];
        for (var i = 0; i <= n; i++)
        {
            best[i] = [];
        }

        best[0].Add((null, 0), new List<SentenceHypothesis>
        {
            SentenceHypothesis.Root(previousWord)
        });

        var arcBuffer = new List<SentenceArc>();
        for (var i = 0; i < n; i++)
        {
            if (best[i].Count == 0)
            {
                continue;
            }

            arcBuffer.Clear();
            provider.AppendArcs(i, arcBuffer);

            // 预算超限时优先保留真实弧里靠前的（posting 已按词频排），但始终为兜底弧留一个位置。
            var room = Math.Max(1, _options.MaxArcsPerStart - 1);
            var capacity = Math.Max(1, _options.MaxTotalArcs - totalArcs - 1);
            var keep = Math.Min(room, capacity);
            if (arcBuffer.Count > keep)
            {
                arcBuffer.RemoveRange(keep, arcBuffer.Count - keep);
                timedOut = true;
            }

            AppendFallback(input, i, arcBuffer);
            var clipped = arcBuffer;
            totalArcs += clipped.Count;

            foreach (var arc in clipped)
            {
                if (arc.Length <= 0 || i + arc.Length > n)
                {
                    continue; // 防御：非法弧直接忽略，避免死循环或越界。
                }

                var j = i + arc.Length;
                if (CrossesHardBoundary(input, i, j))
                {
                    continue;
                }

                var arcSyllables = arc.IsFallback ? [] : T9Engine.Syllables(arc.Pinyin);
                foreach (var state in best[i])
                {
                    foreach (var hypothesis in state.Value)
                    {
                        // 兜底弧是完整覆盖的安全网，不受转移预算限制，确保任何情况下都能走到末尾。
                        if (transitions >= _options.MaxTransitions && !arc.IsFallback)
                        {
                            timedOut = true;
                            continue;
                        }

                        if (!ConfirmedMatches(confirmedSyllables, hypothesis.ConfirmedProgress, arcSyllables))
                        {
                            continue;
                        }

                        transitions++;
                        var progress = ConfirmedProgress(confirmedSyllables, hypothesis.ConfirmedProgress, arcSyllables.Count);
                        var next = new SentenceHypothesis
                        {
                            Score = hypothesis.Score + scorer.Score(arc, hypothesis.LastWord),
                            Previous = hypothesis,
                            Arc = arc,
                            Start = i,
                            End = j,
                            LastWord = arc.IsFallback ? null : arc.Word,
                            ConfirmedProgress = progress,
                            HasFallback = hypothesis.HasFallback || arc.IsFallback,
                            HasPrefix = hypothesis.HasPrefix || arc.Kind == SentenceArcKind.Prefix
                        };
                        InsertPruned(best[j], (next.LastWord, progress), next);
                    }
                }

                if (transitions % 128 == 0 && _clock.GetElapsedTime(timestamp).TotalMilliseconds > _options.TimeBudgetMilliseconds)
                {
                    timedOut = true;
                }
            }

            // 每个输入位置（最多 64 个）也查一次，保证小输入在密集弧下也能及时判超时。
            if (_clock.GetElapsedTime(timestamp).TotalMilliseconds > _options.TimeBudgetMilliseconds)
            {
                timedOut = true;
            }
        }

        var paths = BuildPaths(best[n], n, pathCount);
        return new SentenceDecodeResult(paths, timedOut, transitions, totalArcs);
    }

    private void InsertPruned(
        Dictionary<(string? LastWord, int Progress), List<SentenceHypothesis>> slot,
        (string? LastWord, int Progress) key,
        SentenceHypothesis candidate)
    {
        if (!slot.TryGetValue(key, out var list))
        {
            list = [];
            slot[key] = list;
        }

        // 同状态只保留 PathCount 条最优；超出后还要受每位置总状态数约束。
        if (list.Count < _options.PathCount)
        {
            list.Add(candidate);
        }
        else
        {
            var worst = MinIndex(list);
            if (candidate.Score > list[worst].Score)
            {
                list[worst] = candidate;
            }
        }

        if (slot.Count > _options.MaxStatesPerPosition)
        {
            TrimStates(slot);
        }

        TrimToGlobalCap(slot);
    }

    /// <summary>
    /// 按末词分状态后假设数会组合膨胀（弧数 × 末词数）。这里对整个位置做束剪枝：
    /// 每个状态至少保留其最优一条，其余按全局分数从低到高淘汰，把单位置假设数夹到上限。
    /// </summary>
    private void TrimToGlobalCap(Dictionary<(string? LastWord, int Progress), List<SentenceHypothesis>> slot)
    {
        var total = slot.Values.Sum(list => list.Count);
        if (total <= _options.MaxHypothesesPerPosition)
        {
            return;
        }

        var removable = new List<(double Score, SentenceHypothesis Hypothesis)>();
        foreach (var list in slot.Values)
        {
            var best = list[0];
            foreach (var hypothesis in list)
            {
                if (hypothesis.Score > best.Score)
                {
                    best = hypothesis;
                }
            }

            foreach (var hypothesis in list)
            {
                if (!ReferenceEquals(hypothesis, best))
                {
                    removable.Add((hypothesis.Score, hypothesis));
                }
            }
        }

        var removeCount = total - _options.MaxHypothesesPerPosition;
        var toRemove = removable
            .OrderBy(item => item.Score)
            .Take(removeCount)
            .Select(item => item.Hypothesis)
            .ToHashSet();
        foreach (var list in slot.Values)
        {
            list.RemoveAll(toRemove.Contains);
        }

        var empty = slot.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList();
        foreach (var key in empty)
        {
            slot.Remove(key);
        }
    }

    private void TrimStates(Dictionary<(string?, int), List<SentenceHypothesis>> slot)
    {
        // 状态数超预算时，整体分数最低的状态整条淘汰。
        while (slot.Count > _options.MaxStatesPerPosition)
        {
            var worstKey = default((string?, int));
            var worstScore = double.MaxValue;
            foreach (var pair in slot)
            {
                var top = pair.Value[0].Score;
                if (top < worstScore)
                {
                    worstScore = top;
                    worstKey = pair.Key;
                }
            }

            slot.Remove(worstKey);
        }
    }

    private static int MinIndex(List<SentenceHypothesis> list)
    {
        var index = 0;
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Score < list[index].Score)
            {
                index = i;
            }
        }

        return index;
    }

    private static bool CrossesHardBoundary(SentenceInput input, int start, int end)
    {
        foreach (var boundary in input.HardBoundaries)
        {
            if (boundary > start && boundary < end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ConfirmedMatches(
        IReadOnlyList<string>? confirmed,
        int progress,
        IReadOnlyList<string> arcSyllables)
    {
        if (confirmed is null || confirmed.Count == 0)
        {
            return true;
        }

        // 还有未满足的确认音节时，无音节弧（单键兜底）无法证明匹配，直接排除——
        // 用户已明确选定音节，不应回退成原始数字串。
        if (confirmed.Count > 0 && progress < confirmed.Count && arcSyllables.Count == 0)
        {
            return false;
        }

        for (var k = 0; k < arcSyllables.Count; k++)
        {
            var global = progress + k;
            if (global < confirmed.Count
                && !string.Equals(confirmed[global], arcSyllables[k], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int ConfirmedProgress(
        IReadOnlyList<string>? confirmed,
        int current,
        int addedSyllables)
    {
        if (confirmed is null || confirmed.Count == 0)
        {
            return 0;
        }

        return Math.Min(confirmed.Count, current + addedSyllables);
    }

    private void AppendFallback(SentenceInput input, int start, List<SentenceArc> arcs)
    {
        // 单键兜底：每个位置至少有一条出弧，保证总能走到末尾。
        arcs.Add(new SentenceArc(
            1,
            input.Code[start].ToString(),
            "",
            0,
            SentenceArcKind.Fallback,
            -1));
    }

    private static IReadOnlyList<SentencePath> BuildPaths(
        Dictionary<(string? LastWord, int Progress), List<SentenceHypothesis>> terminal,
        int consumed,
        int take)
    {
        var all = new List<SentenceHypothesis>();
        foreach (var list in terminal.Values)
        {
            all.AddRange(list);
        }

        all.Sort((a, b) => b.Score.CompareTo(a.Score));

        var paths = new List<SentencePath>();
        var seenWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hypothesis in all)
        {
            var segments = WalkBack(hypothesis);
            var word = string.Concat(segments.Select(s => s.Word));
            if (!seenWords.Add(word))
            {
                continue;
            }

            paths.Add(new SentencePath
            {
                Word = word,
                Pinyin = string.Join(' ', segments.Where(s => s.Pinyin.Length > 0).Select(s => s.Pinyin)),
                Score = hypothesis.Score,
                ConsumedCodeLength = consumed,
                Segments = segments,
                HasFallback = hypothesis.HasFallback,
                HasPrefix = hypothesis.HasPrefix
            });

            if (paths.Count >= take)
            {
                break;
            }
        }

        return paths;
    }

    private static IReadOnlyList<SentenceSegment> WalkBack(SentenceHypothesis? hypothesis)
    {
        var reversed = new List<SentenceSegment>();
        var current = hypothesis;
        while (current is not null && current.Previous is not null)
        {
            var arc = current.Arc;
            reversed.Add(new SentenceSegment
            {
                Start = current.Start,
                End = current.End,
                Word = arc.Word,
                Pinyin = arc.Pinyin,
                Kind = arc.Kind,
                Frequency = arc.Frequency
            });
            current = current.Previous;
        }

        reversed.Reverse();
        return reversed;
    }

    private sealed class SentenceHypothesis
    {
        public double Score { get; init; }
        public SentenceHypothesis? Previous { get; init; }
        public SentenceArc Arc { get; init; }
        public int Start { get; init; }
        public int End { get; init; }
        public string? LastWord { get; init; }
        public int ConfirmedProgress { get; init; }
        public bool HasFallback { get; init; }
        public bool HasPrefix { get; init; }

        public static SentenceHypothesis Root(string? previousWord) => new()
        {
            Score = 0,
            Previous = null,
            Arc = default,
            LastWord = previousWord
        };
    }
}
