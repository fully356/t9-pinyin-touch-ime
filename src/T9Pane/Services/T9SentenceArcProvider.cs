namespace T9Pane.Services;

/// <summary>
/// 用 T9Engine 的精确索引把输入码逐位置展开成词图弧。
/// 数字模式查 _exactFull，字母模式查 _exactLetters；末尾未打完的音节走前缀补全。
/// 不做全库扫描，只查精确索引并按 posting（已按词频降序）截断。
/// </summary>
internal sealed class T9SentenceArcProvider : ISentenceArcProvider
{
    private readonly List<T9Entry> _entries;
    private readonly Dictionary<string, List<int>> _exactIndex;
    private readonly Dictionary<string, List<int>> _byFirstSyllable;
    private readonly T9SyllableTable _syllables;
    private readonly SentenceDecoderOptions _options;
    private readonly T9LearnedLexicon? _learned;

    public SentenceInput Input { get; }

    public T9SentenceArcProvider(
        SentenceInput input,
        List<T9Entry> entries,
        Dictionary<string, List<int>> exactIndex,
        Dictionary<string, List<int>> byFirstSyllable,
        T9SyllableTable syllables,
        SentenceDecoderOptions options,
        T9LearnedLexicon? learned = null)
    {
        Input = input;
        _entries = entries;
        _exactIndex = exactIndex;
        _byFirstSyllable = byFirstSyllable;
        _syllables = syllables;
        _options = options;
        _learned = learned;
    }

    // 任何合法拼音音节最长 6 个码（zhuang/chuang/shuang）。
    private const int MaxSyllableSpan = 6;

    public void AppendArcs(int start, List<SentenceArc> destination)
    {
        var code = Input.Code;
        var n = code.Length;
        var maxLength = Math.Min(_options.MaxArcSpan, n - start);
        var realArcCap = Math.Max(8, _options.MaxArcsPerStart - 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 按跨度建桶：每个跨度各自收集自造词、主库词/字，posting 已按词频降序。
        var spanBuckets = new List<List<SentenceArc>>();
        for (var length = 1; length <= maxLength; length++)
        {
            var span = code.Substring(start, length);
            var spanWords = new List<SentenceArc>();
            var spanChars = new List<SentenceArc>();

            // 用户自造词在该跨度优先入桶（即使主库无此精确码也要建桶）。
            _learned?.AppendArcs(Input.Mode, span, spanWords, seen);

            if (_exactIndex.TryGetValue(span, out var posting))
            {
                var wordInSpan = 0;
                var charInSpan = 0;
                foreach (var index in posting)
                {
                    var entry = _entries[index];
                    var isCharacter = entry.Word.Length == 1;
                    var key = (isCharacter ? "c:" : "w:") + entry.Word + ":" + length;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    if (isCharacter)
                    {
                        // 单字只可能落在单音节跨度（≤6）。
                        if (length > MaxSyllableSpan || charInSpan >= _options.MaxCharacterArcsPerSpan)
                        {
                            continue;
                        }

                        spanChars.Add(new SentenceArc(
                            length, entry.Word, entry.Pinyin, entry.Frequency,
                            SentenceArcKind.Character, index));
                        charInSpan++;
                    }
                    else
                    {
                        if (wordInSpan >= _options.MaxWordArcsPerSpan)
                        {
                            continue;
                        }

                        spanWords.Add(new SentenceArc(
                            length, entry.Word, entry.Pinyin, entry.Frequency,
                            SentenceArcKind.Word, index));
                        wordInSpan++;
                    }
                }
            }

            // 同跨度内多字词（含自造词）优先于单字，使轮询第一轮先取到成词。
            if (spanWords.Count > 0 || spanChars.Count > 0)
            {
                var bucket = new List<SentenceArc>(spanWords.Count + spanChars.Count);
                bucket.AddRange(spanWords);
                bucket.AddRange(spanChars);
                spanBuckets.Add(bucket);
            }
        }

        // 跨跨度轮询合并：先每桶取第 1 条，再每桶取第 2 条……保证任何跨度的最优
        // 词/字都不会因短跨度条目过多而被整体饿死，直到用满单起点配额。
        var added = 0;
        var rank = 0;
        while (added < realArcCap)
        {
            var any = false;
            foreach (var bucket in spanBuckets)
            {
                if (rank < bucket.Count)
                {
                    destination.Add(bucket[rank]);
                    added++;
                    any = true;
                    if (added >= realArcCap)
                    {
                        break;
                    }
                }
            }

            rank++;
            if (!any)
            {
                break;
            }
        }

        var prefixCap = Math.Min(_options.MaxPrefixArcsPerSuffix, Math.Max(1, realArcCap / 8));
        AppendTailPrefix(start, destination, seen, Math.Min(prefixCap, realArcCap - added));
    }

    private void AppendTailPrefix(int start, List<SentenceArc> destination, HashSet<string> added, int prefixCap)
    {
        var n = Input.Code.Length;
        if (start >= n || prefixCap <= 0)
        {
            return;
        }

        var rest = Input.Code.Substring(start);
        var completions = Input.Mode == SentenceInputMode.Digits
            ? _syllables.PrefixDigits(rest)
            : _syllables.PrefixLetters(rest);

        var taken = 0;
        foreach (var syllable in completions)
        {
            if (taken >= prefixCap)
            {
                break;
            }

            if (!_byFirstSyllable.TryGetValue(syllable, out var posting))
            {
                continue;
            }

            foreach (var index in posting)
            {
                if (taken >= prefixCap)
                {
                    break;
                }

                var entry = _entries[index];
                // v1 只补全最后一个单字、单音节，避免多字词部分弧爆炸。
                if (entry.Word.Length != 1)
                {
                    continue;
                }

                var key = "p:" + entry.Word + ":" + rest.Length;
                if (!added.Add(key))
                {
                    continue;
                }

                destination.Add(new SentenceArc(
                    rest.Length,
                    entry.Word,
                    syllable,
                    entry.Frequency,
                    SentenceArcKind.Prefix,
                    index));
                taken++;
            }
        }
    }
}
