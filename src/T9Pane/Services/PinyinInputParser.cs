using System.Text;

namespace T9Pane.Services;

/// <summary>
/// 把原始按键规范化成解码输入。字母模式在 compact（折声调、去分隔符）之前，
/// 先记录隔音符/连字符位置作为硬边界，避免 xi'an 的边界被 CompactLetters 抹掉。
/// </summary>
internal static class PinyinInputParser
{
    public static bool TryDigits(string? raw, out SentenceInput input)
    {
        input = null!;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var code = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is >= '2' and <= '9')
            {
                code.Append(ch);
            }
        }

        if (code.Length == 0)
        {
            return false;
        }

        input = new SentenceInput
        {
            Code = code.ToString(),
            Mode = SentenceInputMode.Digits
        };
        return true;
    }

    public static bool TryLetters(string? raw, out SentenceInput input)
    {
        input = null!;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var code = new StringBuilder(raw.Length);
        var boundaries = new HashSet<int>();
        foreach (var rawCh in raw)
        {
            if (rawCh is ' ' or '\'' or '’' or '-')
            {
                if (code.Length > 0)
                {
                    boundaries.Add(code.Length);
                }

                continue;
            }

            var ch = T9Engine.FoldPinyin(rawCh);
            if (ch is >= 'a' and <= 'z' or 'v')
            {
                code.Append(ch);
            }
        }

        if (code.Length == 0)
        {
            return false;
        }

        input = new SentenceInput
        {
            Code = code.ToString(),
            Mode = SentenceInputMode.Letters,
            HardBoundaries = boundaries
        };
        return true;
    }

    /// <summary>区分模式、码和硬边界的缓存键，确保 xian 与 xi'an 不共用缓存。</summary>
    public static string CacheKey(SentenceInput input)
    {
        var prefix = input.Mode == SentenceInputMode.Digits ? "D:" : "L:";
        if (input.HardBoundaries.Count == 0)
        {
            return prefix + input.Code;
        }

        var boundaries = string.Join(",", input.HardBoundaries.OrderBy(position => position));
        return prefix + input.Code + "|b" + boundaries;
    }
}
