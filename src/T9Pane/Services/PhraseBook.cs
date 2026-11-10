using System.IO;
using System.Text;

namespace T9Pane.Services;

internal sealed class PhraseEntry
{
    /// <summary>用户输入的快捷键原文（字母或数字）。</summary>
    public required string Shortcut { get; init; }

    /// <summary>要上屏的内容。</summary>
    public required string Content { get; init; }
}

/// <summary>
/// 自定义快捷输入。文件 %APPDATA%\T9Pane\phrases.txt，一行一条：
///     快捷键&lt;TAB&gt;要上屏的内容
///
/// 快捷键可以写字母（微信输入法那种，如 dz），也可以写数字。
/// 匹配时统一折算成九宫格数字串（字母 a-z → 2~9），因为九宫格上按的就是数字。
/// 注意：拼音也折成 2~9，所以快捷键越短越容易和拼音撞，
/// 一撞上就是「一敲就上屏」。建议字母用三位以上，或直接用重复数字（2222 / 999）。
/// </summary>
internal static class PhraseBook
{
    /// <summary>短于此长度的快捷键不触发，避免一位数误上屏。</summary>
    public const int MinCodeLength = 2;

    private static readonly object Gate = new();
    private static List<PhraseEntry> _entries = [];
    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static string _stamp = "";

    public static string Path => AppSettings.PhrasePath;

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return _entries.Count;
            }
        }
    }

    public static IReadOnlyList<PhraseEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public static bool TryGet(string? code, out string phrase)
    {
        phrase = "";
        if (string.IsNullOrEmpty(code) || code.Length < MinCodeLength)
        {
            return false;
        }

        lock (Gate)
        {
            return _map.TryGetValue(code, out phrase!);
        }
    }

    /// <summary>
    /// 只留数字：字母按九宫格折成数字，数字原样保留，其余字符丢弃。
    /// 不能用 CompactLetters —— 它只保留 a-z，会把快捷键里的数字全部扔掉。
    /// </summary>
    public static string Normalize(string code) => T9Engine.ToDigits(code ?? "");

    public static void Reload()
    {
        lock (Gate)
        {
            _entries = Parse(Path);
            Rebuild();
            _stamp = Stamp();
        }
    }

    /// <summary>按文件时间戳判断要不要重读，用户改完文件不用重启输入法。</summary>
    public static void ReloadIfChanged()
    {
        string stamp;
        try
        {
            stamp = Stamp();
        }
        catch
        {
            return;
        }

        lock (Gate)
        {
            if (stamp == _stamp)
            {
                return;
            }

            _entries = Parse(Path);
            Rebuild();
            _stamp = stamp;
        }
    }

    /// <summary>新增或覆盖一条。快捷键折算后不足 MinCodeLength，或内容为空，都拒绝。</summary>
    public static bool Upsert(string shortcut, string content)
    {
        var text = (content ?? "").Trim();
        var raw = (shortcut ?? "").Trim();
        if (Normalize(raw).Length < MinCodeLength || text.Length == 0)
        {
            return false;
        }

        lock (Gate)
        {
            var key = Normalize(raw);
            _entries.RemoveAll(entry => Normalize(entry.Shortcut) == key);
            _entries.Insert(0, new PhraseEntry { Shortcut = raw, Content = text });
            Rebuild();
        }

        return true;
    }

    public static bool Remove(string shortcut)
    {
        var key = Normalize(shortcut);
        lock (Gate)
        {
            if (_entries.RemoveAll(entry => Normalize(entry.Shortcut) == key) == 0)
            {
                return false;
            }

            Rebuild();
        }

        return true;
    }

    /// <summary>整份回写。文件由本编辑框管理，手工写的注释不会保留。</summary>
    public static void Save()
    {
        List<PhraseEntry> snapshot;
        lock (Gate)
        {
            snapshot = _entries.ToList();
        }

        try
        {
            var sb = new StringBuilder(Header);
            foreach (var entry in snapshot)
            {
                sb.Append(entry.Shortcut).Append('\t').Append(entry.Content).Append('\n');
            }

            File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));
            lock (Gate)
            {
                _stamp = Stamp();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"保存常用短语失败: {ex.Message}");
        }
    }

    public static void EnsureTemplate()
    {
        try
        {
            if (File.Exists(Path))
            {
                return;
            }

            Directory.CreateDirectory(AppSettings.AppDataDirectory);
            File.WriteAllText(Path, Header, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn($"创建常用短语模板失败: {ex.Message}");
        }
    }

    private static void Rebuild()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            var key = Normalize(entry.Shortcut);
            if (key.Length >= MinCodeLength)
            {
                map[key] = entry.Content;
            }
        }

        _map = map;
    }

    private static string Stamp() =>
        File.Exists(Path) ? File.GetLastWriteTimeUtc(Path).Ticks.ToString() : "none";

    private static List<PhraseEntry> Parse(string path)
    {
        var list = new List<PhraseEntry>();
        try
        {
            if (!File.Exists(path))
            {
                return list;
            }

            foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 2)
                {
                    // 也接受「快捷键 内容」「快捷键=内容」，方便直接在手机上编辑。
                    var cut = line.IndexOfAny([' ', '=', '，', ',', '：', ':']);
                    if (cut <= 0)
                    {
                        continue;
                    }

                    parts = [line[..cut], line[(cut + 1)..]];
                }

                var shortcut = parts[0].Trim();
                var content = parts[1].Trim();
                if (Normalize(shortcut).Length < MinCodeLength || content.Length == 0)
                {
                    continue;
                }

                list.Add(new PhraseEntry { Shortcut = shortcut, Content = content });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取常用短语失败: {ex.Message}");
        }

        return list;
    }

    private const string Header =
        "# 自定义快捷输入：一行一条，快捷键和内容之间用 Tab 分隔\n" +
        "# 这个文件由键盘上的「快捷」编辑框管理，手工写的注释不会保留。\n" +
        "#\n" +
        "# 快捷键可以写字母（如 dz），也可以写数字。九宫格上按的就是数字，\n" +
        "# 字母会折算成 2~9，所以快捷键越短越容易和拼音撞：\n" +
        "#   两位数极易冲突（24 = 爱/比/才，64 = 你/迷/尼），一撞就「一敲就上屏」；\n" +
        "#   建议字母用三位以上，或直接用重复数字（2222 / 999）。\n" +
        "#\n" +
        "# 想临时关掉自动上屏：右键托盘 T9 图标 → 取消勾选「简码自动上屏」。\n";
}
