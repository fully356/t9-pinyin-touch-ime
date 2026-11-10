namespace T9Pane.Services;

/// <summary>
/// 九键排序：全拼、组句、全拼前缀，然后才是组词和简拼。
/// 组词/组句按「还剩多少码没吃掉」排，吃得越干净越靠前。
/// </summary>
internal static class T9MatchRank
{
    public static int Kind(string kind) => kind switch
    {
        "短语" => 8,
        "短语前缀" => 7,
        "全拼" => 6,
        "整句" => 5,
        "组句" => 5,
        "全拼前缀" => 4,
        "组词" => 3,
        "简拼" => 2,
        _ => 1
    };

    public static int Leftover(string kind, int codeLength, int typedLength) =>
        kind is "组词" or "组句" or "整句" or "简拼组词"
            ? Math.Max(0, typedLength - codeLength)
            : Math.Max(0, codeLength - typedLength);

    public static IReadOnlyList<T9Candidate> Order(
        IEnumerable<T9Candidate> hits,
        int typedLength,
        int take)
    {
        return hits
            .OrderByDescending(hit => Kind(hit.MatchKind))
            .ThenBy(hit => Leftover(hit.MatchKind, EffectiveCodeLength(hit), typedLength))
            // 带 Viterbi 分的整句按概率分排序（其 Frequency 是跨字/词池的频率和，不可比）；
            // 旧候选 Score 全为 null，在此键并列后落到下一键按词频，历史排序完全不变。
            .ThenByDescending(hit => hit.Score ?? double.NegativeInfinity)
            .ThenByDescending(hit => hit.Frequency)
            .ThenBy(hit => hit.Word.Length)
            .Take(take)
            .ToList();
    }

    /// <summary>前缀弧的拼音比实际输入长，整句用 ConsumedCodeLength；旧候选回退到拼音数字长度。</summary>
    private static int EffectiveCodeLength(T9Candidate hit) =>
        hit.ConsumedCodeLength > 0 ? hit.ConsumedCodeLength : T9Engine.ToDigits(hit.Pinyin).Length;
}
