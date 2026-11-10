namespace T9Pane.Services;

internal enum FullKeyAction
{
    Text,
    Letter,
    Backspace,
    Enter,
    Space,
    Shift,
    Caps,
    Lang,
    Predict,
    Esc,
    Tab,
    Delete,
    Left,
    Right,
    Up,
    Down,
    Ctrl,
    Alt,
    Win,
    Symbol,
    Function
}

internal readonly record struct FullKeySpec(
    string Label,
    double Units,
    FullKeyAction Action,
    string? ShiftLabel = null,
    string? Payload = null)
{
    public string Emit(bool shift) =>
        shift && !string.IsNullOrEmpty(ShiftLabel) ? ShiftLabel : Payload ?? Label;
}

/// <summary>
/// 全键盘按 16 个方格单位排。字母/数字键占 1 格，因此单元格是正方形；
/// Shift、空格、回车只加宽，不加高。
/// </summary>
internal static class FullKeyboardLayout
{
    public const double Units = 16;
    public const int RowCount = 5;

    public static (double Width, double Height) FitCells(
        double availableWidth,
        double availableHeight,
        double columns,
        double rows)
    {
        if (availableWidth <= 0 || availableHeight <= 0 || columns <= 0 || rows <= 0)
        {
            return (0, 0);
        }

        var unit = Math.Min(availableWidth / columns, availableHeight / rows);
        return (unit * columns, unit * rows);
    }

    public static IReadOnlyList<IReadOnlyList<FullKeySpec>> Rows(bool latin, bool fn = false)
    {
        return
        [
            NumberRow(fn),
            [
                Key("Tab", 1.5, FullKeyAction.Tab),
                Letter("q"), Letter("w"), Letter("e"), Letter("r"), Letter("t"),
                Letter("y"), Letter("u"), Letter("i"), Letter("o"), Letter("p"),
                Dual("[", "{", 1),
                Dual("]", "}", 1),
                Dual("\\", "|", 1),
                Key("Del", 1.5, FullKeyAction.Delete)
            ],
            [
                Key("Caps", 2, FullKeyAction.Caps),
                Letter("a"), Letter("s"), Letter("d"), Letter("f"), Letter("g"),
                Letter("h"), Letter("j"), Letter("k"), Letter("l"),
                Dual(";", ":", 1),
                Dual("'", "\"", 1),
                Key("回车", 3, FullKeyAction.Enter)
            ],
            [
                Key("Shift", 2.25, FullKeyAction.Shift),
                Letter("z"), Letter("x"), Letter("c"), Letter("v"), Letter("b"),
                Letter("n"), Letter("m"),
                Dual(",", "<", 1),
                Dual(".", ">", 1),
                Dual("/", "?", 1),
                Key("▲", 1, FullKeyAction.Up),
                Key("Shift", 2.75, FullKeyAction.Shift)
            ],
            BottomRow(latin)
        ];
    }

    public static double RowUnits(IEnumerable<FullKeySpec> row) =>
        row.Sum(key => key.Units);

    public static (string Primary, string? Secondary) Face(
        FullKeySpec spec,
        bool shift,
        bool caps)
    {
        if (spec.Action == FullKeyAction.Letter)
        {
            var letter = spec.Payload ?? spec.Label;
            return ((shift ^ caps) ? letter.ToUpperInvariant() : letter.ToLowerInvariant(), null);
        }

        if (spec.Action == FullKeyAction.Text && !string.IsNullOrEmpty(spec.ShiftLabel))
        {
            return shift ? (spec.ShiftLabel, spec.Label) : (spec.Label, spec.ShiftLabel);
        }

        return (spec.Label, spec.ShiftLabel);
    }

    private static IReadOnlyList<FullKeySpec> BottomRow(bool latin)
    {
        var lang = latin ? "EN" : "简体";
        var row = new List<FullKeySpec>
        {
            Key("Ctrl", 1.25, FullKeyAction.Ctrl),
            Key("Fn", 1.25, FullKeyAction.Symbol),
            Key("⊞", 1.25, FullKeyAction.Win),
            Key("Alt", 1.25, FullKeyAction.Alt),
            Key("空格", latin ? 3.25 : 4.25, FullKeyAction.Space),
            Key("Alt", 1.25, FullKeyAction.Alt),
            Key("Ctrl", 1.25, FullKeyAction.Ctrl),
            Key("◀", 1, FullKeyAction.Left),
            Key("▼", 1, FullKeyAction.Down),
            Key("▶", 1, FullKeyAction.Right),
            Key(lang, 1.25, FullKeyAction.Lang)
        };
        if (latin)
        {
            row.Add(Key(EnglishPredictPolicy.Label, 1, FullKeyAction.Predict));
        }

        return row;
    }

    private static IReadOnlyList<FullKeySpec> NumberRow(bool fn)
    {
        if (fn)
        {
            return
            [
                Key("Esc", 1, FullKeyAction.Esc),
                Dual("`", "~", 1),
                Fn("F1"), Fn("F2"), Fn("F3"), Fn("F4"),
                Fn("F5"), Fn("F6"), Fn("F7"), Fn("F8"),
                Fn("F9"), Fn("F10"), Fn("F11"), Fn("F12"),
                Key("⌫", 2, FullKeyAction.Backspace)
            ];
        }

        return
        [
            Key("Esc", 1, FullKeyAction.Esc),
            Dual("1", "!", 1),
            Dual("2", "@", 1),
            Dual("3", "#", 1),
            Dual("4", "$", 1),
            Dual("5", "%", 1),
            Dual("6", "^", 1),
            Dual("7", "&", 1),
            Dual("8", "*", 1),
            Dual("9", "(", 1),
            Dual("0", ")", 1),
            Dual("`", "~", 1),
            Dual("-", "_", 1),
            Dual("=", "+", 1),
            Key("⌫", 2, FullKeyAction.Backspace)
        ];
    }

    private static FullKeySpec Key(string label, double units, FullKeyAction action) =>
        new(label, units, action);

    private static FullKeySpec Fn(string name) =>
        new(name, 1, FullKeyAction.Function, Payload: name);

    private static FullKeySpec Letter(string letter) =>
        new(letter, 1, FullKeyAction.Letter, Payload: letter);

    private static FullKeySpec Dual(string label, string shift, double units) =>
        new(label, units, FullKeyAction.Text, shift, label);
}

/// <summary>
/// 九键字母面必须同一字号。Viewbox 会按每个标签自己缩放，
/// ABC 显大、WXYZ / PQRS 显小。字号按最长标签能在方格里放下来定。
/// </summary>
internal static class T9KeyFace
{
    public static readonly string[] Labels =
    [
        "分词", "ABC", "DEF", "GHI", "JKL", "MNO", "PQRS", "TUV", "WXYZ"
    ];

    // 基准字号。实际字号再乘 KeyboardChromeSize.UiScale，让文字跟盘面一起变大变小。
    private const double BaseFontSize = 19;
    private const double BaseEnglishFontSize = 24;
    private const double BaseSymbolFontSize = 26;
    private const double BaseNumberFontSize = 22;

    public const bool SymbolSemiBold = false;

    public static double FontSize => BaseFontSize * KeyboardChromeSize.UiScale;
    public static double EnglishFontSize => BaseEnglishFontSize * KeyboardChromeSize.UiScale;
    public static double SymbolFontSize => BaseSymbolFontSize * KeyboardChromeSize.UiScale;
    public static double NumberFontSize => BaseNumberFontSize * KeyboardChromeSize.UiScale;

    public static double FaceSizeFor(string _) => FontSize;
}

/// <summary>
/// 盘面宽度随模式变：九键按方键收窄，全键盘才加宽。不要共用一个固定宽。
/// </summary>
internal static class KeyboardChromeSize
{
    /// <summary>用户可调的整盘缩放（托盘「调整键盘大小」）。1.0 = 原始尺寸。</summary>
    public const double MinUiScale = 0.7;
    public const double MaxUiScale = 1.6;

    private static double _uiScale = 1.0;

    /// <summary>
    /// 全盘统一缩放。所有尺寸常量都乘它，所以命中映射
    /// （HostHitMap 按 layout/frame 比例换算）自动保持一致。
    /// </summary>
    public static double UiScale
    {
        get => _uiScale;
        set => _uiScale = ClampUiScale(value);
    }

    public static double ClampUiScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinUiScale, MaxUiScale) : 1.0;

    /// <summary>
    /// 只放大九键 3×3 和英文 26 键。标题、候选、功能键、数字/符号保持原尺寸。
    /// </summary>
    public const double PadScale = 1.2;
    public static double FramePad => 16 * UiScale;
    public static double Title => 38 * UiScale;
    public static double Candidate => 34 * UiScale;
    public static double CandidateButton => 28 * UiScale;
    public static double CandidateCell => 38 * UiScale;
    public static double Function => 52 * UiScale;
    public static double CompactWidth => 400 * UiScale;
    public static double CompactBoard => 220 * UiScale;
    public static double T9Board => CompactBoard * PadScale;
    public static double CompactHeight => FramePad + Title + Candidate + Function + T9Board;
    public static double EnglishRail => 64 * UiScale;
    public static double EnglishLetterUnit => 63.6 * PadScale * UiScale;
    public static double EnglishWidth => FramePad + EnglishRail * 2 + EnglishLetterUnit * 10;
    public static double SymbolRail => 52 * UiScale;
    public static double FullWidth => 840 * PadScale * UiScale;
    public const double NumberColumns = 3;
    public const double NumberRows = 4;

    public static double FittedHeight(
        double width,
        double columns,
        double rows,
        bool functionBar)
    {
        var board = Math.Max(1, width - FramePad);
        var unit = board / columns;
        return FramePad + Title + Candidate + (functionBar ? Function : 0) + unit * rows;
    }

    public static double FullHeight =>
        FittedHeight(FullWidth, FullKeyboardLayout.Units, FullKeyboardLayout.RowCount, false);

    /// <summary>
    /// 数字盘保持未放大的方格。旧算法按 CompactWidth 铺 3 列，
    /// 键几乎比 T9 大一倍。
    /// </summary>
    public static double CompactUnit => CompactBoard / 3;

    /// <summary>
    /// 数字盘窗口跟九键一样宽，标题和底栏才显示得下。
    /// 键本身仍用 CompactUnit，居中，不再把整窗压到三列宽度。
    /// </summary>
    public static double NumberWidth => CompactWidth;

    public static double NumberHeight =>
        FramePad + Title + Candidate + Function + CompactUnit * NumberRows;

    public static (double Width, double Height) ForBoard(
        bool fullKeyboard,
        bool numberPad = false,
        bool english = false) =>
        fullKeyboard
            ? (FullWidth, FullHeight)
            : numberPad
                ? (NumberWidth, NumberHeight)
                : english
                    ? (EnglishWidth, EnglishHeight)
                    : (CompactWidth, CompactHeight);

    public static (double Rail, double Board) EnglishColumns()
    {
        var board = EnglishWidth - FramePad - EnglishRail * 2;
        return (EnglishRail, board);
    }

    /// <summary>英文 26 键按列宽取方格，三行刚好接近正方形。</summary>
    public static double EnglishBoardHeight => EnglishLetterUnit * 3;

    public static double EnglishHeight =>
        FramePad + Title + Candidate + Function + EnglishBoardHeight;

    public static (double Rail, double Board) CompactColumns()
    {
        var board = T9Board;
        var rail = (CompactWidth - FramePad - board) / 2;
        return (rail, board);
    }

    /// <summary>
    /// 符号盘左侧只要放下两个字的分类名，把宽度留给 5 列符号格。
    /// </summary>
    public static (double Rail, double Board) SymbolColumns()
    {
        var board = CompactWidth - FramePad - SymbolRail;
        return (SymbolRail, board);
    }
}
