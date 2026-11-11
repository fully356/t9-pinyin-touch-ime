namespace T9Pane.Services;

/// <summary>
/// 用户自造词的独立精确索引。与 160 万条主词库分开，自造词集合变化时只重建这个小索引，
/// 不触发主词库 Load。数字码查 FullDigits、字母码查 compact 拼音，供弧提供者并入词图。
/// </summary>
internal sealed class T9LearnedLexicon
{
    private readonly List<LearnedCustomWord> _words = [];
    private readonly Dictionary<string, List<SentenceArc>> _byDigits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SentenceArc>> _byLetters = new(StringComparer.Ordinal);
    private long _builtRevision = -1;

    public int Count
    {
        get { return _words.Count; }
    }

    /// <summary>当学习数据 revision 变化时重建索引；无变化则复用。</summary>
    public void EnsureCurrent(IReadOnlyList<LearnedCustomWord> customWords, long revision)
    {
        if (revision == _builtRevision)
        {
            return;
        }

        _words.Clear();
        _byDigits.Clear();
        _byLetters.Clear();
        _words.AddRange(customWords);

        for (var i = 0; i < _words.Count; i++)
        {
            var word = _words[i];
            var digits = T9Engine.ToDigits(word.Pinyin);
            var letters = T9Engine.CompactLetters(word.Pinyin);
            if (digits.Length > 0)
            {
                AddArc(_byDigits, digits, word, digits.Length, SentenceArcKind.Word);
            }

            if (letters.Length > 0)
            {
                AddArc(_byLetters, letters, word, letters.Length, SentenceArcKind.Word);
            }
        }

        _builtRevision = revision;
    }

    private static void AddArc(
        Dictionary<string, List<SentenceArc>> index,
        string key,
        LearnedCustomWord word,
        int length,
        SentenceArcKind kind)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        // 自造词用负 EntryId 与主词库（非负下标）区分，弧去重不依赖它。
        list.Add(new SentenceArc(length, word.Word, word.Pinyin, 1_000 + word.Count, kind, -1));
    }

    /// <summary>追加自造词弧到指定起点的弧表（数字或字母模式）。</summary>
    public void AppendArcs(SentenceInputMode mode, string span, List<SentenceArc> destination, HashSet<string> added)
    {
        var index = mode == SentenceInputMode.Digits ? _byDigits : _byLetters;
        if (!index.TryGetValue(span, out var arcs))
        {
            return;
        }

        foreach (var arc in arcs)
        {
            var key = "u:" + arc.Word + ":" + arc.Length;
            if (added.Add(key))
            {
                destination.Add(arc);
            }
        }
    }
}
