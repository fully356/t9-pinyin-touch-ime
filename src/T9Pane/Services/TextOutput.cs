using T9Pane.Native;

namespace T9Pane.Services;

internal static class TextOutput
{
    public static void SendText(string text, IntPtr targetWindow)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Unicode(ch, keyUp: false));
            inputs.Add(Unicode(ch, keyUp: true));
        }

        Dispatch(inputs);
    }

    public static void SendVirtualKey(ushort vk, IntPtr targetWindow)
    {
        if (vk == NativeMethods.VkBack)
        {
            PostBackspace(targetWindow);
            return;
        }

        if (vk == NativeMethods.VkReturn)
        {
            PostReturn(targetWindow);
            return;
        }

        Dispatch(
        [
            Virtual(vk, keyUp: false),
            Virtual(vk, keyUp: true)
        ]);
    }

    private static readonly HashSet<ushort> HeldKeys = [];

    public static bool HoldKey(ushort vk)
    {
        if (!HeldKeys.Add(vk))
        {
            return false;
        }

        Dispatch([Virtual(vk, keyUp: false)]);
        return true;
    }

    public static bool ReleaseKey(ushort vk)
    {
        if (!HeldKeys.Remove(vk))
        {
            return false;
        }

        Dispatch([Virtual(vk, keyUp: true)]);
        return true;
    }

    public static void PulseKey(ushort vk) =>
        Dispatch(
        [
            Virtual(vk, keyUp: false),
            Virtual(vk, keyUp: true)
        ]);

    public static void ReleaseAllKeys()
    {
        foreach (var vk in HeldKeys.ToArray())
        {
            ReleaseKey(vk);
        }
    }

    public static bool ClickVirtual(ushort vk, ushort? modifier = null)
    {
        if (modifier is { } held)
        {
            return Dispatch(
            [
                Virtual(held, keyUp: false),
                Virtual(vk, keyUp: false),
                Virtual(vk, keyUp: true),
                Virtual(held, keyUp: true)
            ]);
        }

        return Dispatch(
        [
            Virtual(vk, keyUp: false),
            Virtual(vk, keyUp: true)
        ]);
    }

    public static void SendChord(IReadOnlyList<ushort> modifiers, ushort vk)
    {
        if (modifiers.Count == 0)
        {
            SendVirtualKey(vk, default);
            return;
        }

        var inputs = new List<INPUT>(modifiers.Count * 2 + 2);
        foreach (var modifier in modifiers)
        {
            inputs.Add(Virtual(modifier, keyUp: false));
        }

        inputs.Add(Virtual(vk, keyUp: false));
        inputs.Add(Virtual(vk, keyUp: true));
        for (var i = modifiers.Count - 1; i >= 0; i--)
        {
            inputs.Add(Virtual(modifiers[i], keyUp: true));
        }

        Dispatch(inputs);
    }

    public static void PostReturn(IntPtr targetWindow = default)
    {
        var hwnd = ResolveEditHwnd(targetWindow);
        if (hwnd == IntPtr.Zero)
        {
            Dispatch(
            [
                Virtual(NativeMethods.VkReturn, keyUp: false),
                Virtual(NativeMethods.VkReturn, keyUp: true)
            ]);
            return;
        }

        // 投进目标队列，让那边的 TranslateMessage 生成唯一的 WM_CHAR。
        // SendMessage(KEYDOWN)+CHAR 或 Post(KEYDOWN)+CHAR 都会让 Word 换两行。
        var scan = NativeMethods.MapVirtualKey(NativeMethods.VkReturn, 0) << 16;
        NativeMethods.PostMessage(
            hwnd,
            NativeMethods.WmKeyDown,
            new IntPtr(NativeMethods.VkReturn),
            new IntPtr(unchecked((int)(1u | scan))));
        NativeMethods.PostMessage(
            hwnd,
            NativeMethods.WmKeyUp,
            new IntPtr(NativeMethods.VkReturn),
            new IntPtr(unchecked((int)(0xC0000001u | scan))));
    }

    public static void PostBackspace(IntPtr targetWindow = default)
    {
        // 用 SendInput 注入**真实**键盘事件。
        //
        // 原来走 SendMessage(WM_KEYDOWN/WM_CHAR/WM_KEYUP)：原生 Win32（记事本）
        // 和 Electron（DSH）都收，但 **Windows Terminal 这类 XAML/WinUI 应用的输入栈
        // 只读操作系统输入队列，会直接忽略别的进程塞进来的窗口消息** ——
        // 表现就是「能打字、不能删字」（用户的 Qwen 本地助手跑在 Windows Terminal 里）。
        // 补扫描码也没用：问题不在消息内容，在投递通道。
        // SendInput 进的是系统输入队列，三类应用都认。
        _ = targetWindow;
        var ok = Dispatch([Virtual(NativeMethods.VkBack, keyUp: false), Virtual(NativeMethods.VkBack, keyUp: true)]);
        Log.Info($"[诊断] 退格 → SendInput ok={ok} uiAccess={UiAccessToken.Has()}");
    }

    public static void PostBackspaceLegacy(IntPtr targetWindow = default)
    {
        var hwnd = ResolveEditHwnd(targetWindow);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // lParam 的 bit16~23 是扫描码。原来这里是 1（扫描码 0），
        // 而 Chromium/Electron 系客户端会**拒收扫描码为 0 的键盘消息**（拿它判定消息是否合成），
        // 于是「能打字、不能删字」—— Qwen 本地助手就是这么中招的。
        // 记事本这类原生 Win32 应用不校验扫描码，所以一直看不出问题。
        // VK_BACK 的扫描码是 0x0E；keyup 另外要置 bit30（前次键状态）与 bit31（转换状态）。
        const int backScan = 0x0E << 16;
        NativeMethods.SendMessage(hwnd, NativeMethods.WmKeyDown, new IntPtr(NativeMethods.VkBack), new IntPtr(backScan | 1));
        NativeMethods.SendMessage(hwnd, NativeMethods.WmChar, new IntPtr(8), new IntPtr(backScan | 1));
        NativeMethods.SendMessage(hwnd, NativeMethods.WmKeyUp, new IntPtr(NativeMethods.VkBack), new IntPtr(backScan | unchecked((int)0xC0000001)));
    }

    private static IntPtr ResolveEditHwnd(IntPtr targetWindow)
    {
        var hwnd = IntPtr.Zero;
        var info = new GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
        if (NativeMethods.GetGUIThreadInfo(0, ref info))
        {
            hwnd = info.Focus != IntPtr.Zero ? info.Focus : info.Caret;
        }

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            hwnd = NativeMethods.GetFocus();
        }

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            hwnd = targetWindow;
        }

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return IntPtr.Zero;
        }

        return hwnd;
    }

    public static void SendChord(ushort modifier, ushort vk) =>
        SendChord([modifier], vk);

    public static void SendDigit(char digit, IntPtr targetWindow, bool numpad)
    {
        if (digit is < '0' or > '9')
        {
            return;
        }

        if (numpad)
        {
            var vk = (ushort)(NativeMethods.VkNumpad0 + (digit - '0'));
            SendVirtualKey(vk, targetWindow);
            return;
        }

        SendText(digit.ToString(), targetWindow);
    }

    private static bool Dispatch(IReadOnlyList<INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return false;
        }

        var array = inputs.ToArray();
        var size = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();
        var sent = NativeMethods.SendInput((uint)array.Length, array, size);
        if (sent != array.Length)
        {
            Log.Warn(
                $"SendInput sent={sent} want={array.Length} size={size} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} uiAccess={UiAccessToken.Has()}");
            return false;
        }

        return true;
    }

    private static INPUT Unicode(char ch, bool keyUp)
    {
        return new INPUT
        {
            Type = NativeMethods.InputKeyboard,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT
                {
                    Vk = 0,
                    Scan = ch,
                    Flags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0)
                }
            }
        };
    }

    private static INPUT Virtual(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            Type = NativeMethods.InputKeyboard,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT
                {
                    Vk = vk,
                    Scan = (ushort)NativeMethods.MapVirtualKey(vk, 0),
                    Flags = (keyUp ? NativeMethods.KeyeventfKeyup : 0)
                        | (FullKeyVirtuals.IsExtended(vk) ? NativeMethods.KeyeventfExtendedkey : 0)
                }
            }
        };
    }
}
