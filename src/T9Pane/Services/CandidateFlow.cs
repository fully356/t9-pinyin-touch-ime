namespace T9Pane.Services;

/// <summary>
/// 展开候选面板的行规划：短词仍按固定列数排成紧凑网格；
/// 整句解码产出的长候选（达到阈值字数）独占一整行，避免在 1/5 宽的格子里被裁切。
/// 纯逻辑、不依赖 WPF，便于单测。
/// </summary>
internal sealed record CandidateRow(IReadOnlyList<int> Indices, bool FullWidth);

internal static class CandidateFlow
{
    public const int Columns = 5;

    /// <summary>达到该字数的候选独占整行；短于此的走多列网格。</summary>
    public const int LongWordThreshold = 5;

    /// <summary>全宽行相对普通格的高度倍数，给换行的长句留两行空间。</summary>
    public const double FullRowHeightFactor = 1.8;

    public static bool IsLong(string word) =>
        !string.IsNullOrEmpty(word) && word.Length >= LongWordThreshold;

    public static IReadOnlyList<CandidateRow> Plan(IReadOnlyList<string> words)
    {
        var rows = new List<CandidateRow>();
        var shortBatch = new List<int>();

        void FlushShorts()
        {
            if (shortBatch.Count == 0)
            {
                return;
            }

            // 一批短词按 Columns 切成若干网格行，最后一行允许不满。
            for (var start = 0; start < shortBatch.Count; start += Columns)
            {
                var count = Math.Min(Columns, shortBatch.Count - start);
                rows.Add(new CandidateRow(shortBatch.GetRange(start, count), FullWidth: false));
            }

            shortBatch.Clear();
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (IsLong(words[i]))
            {
                FlushShorts();
                rows.Add(new CandidateRow([i], FullWidth: true));
            }
            else
            {
                shortBatch.Add(i);
            }
        }

        FlushShorts();
        return rows;
    }

    public static double FullRowHeight(double cellHeight) => cellHeight * FullRowHeightFactor;

    public static double ContentHeight(IReadOnlyList<CandidateRow> rows, double cellHeight)
    {
        var total = 0.0;
        foreach (var row in rows)
        {
            total += row.FullWidth ? FullRowHeight(cellHeight) : cellHeight;
        }

        return total;
    }
}
