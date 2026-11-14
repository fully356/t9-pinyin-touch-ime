using System.Runtime.InteropServices;

namespace T9Pane.Services;

/// <summary>
/// 开机/后端启动时把 T9 TIP 激活为当前输入法。
/// 背景：安装脚本只能在安装那一刻激活，Windows 重新登录后活动输入法会回到微软拼音，
/// 而后端随开机自启，故在启动序列里再激活一次，避免重启后键盘不弹。
/// 仅在启动时调用一次：当前已是 T9 就不动，用户运行中手动切走不会被抢回。
/// 任何失败只记日志、不阻断启动（仍可手动 Win+空格 切换）。
/// </summary>
internal static class InputProfileActivator
{
    private static readonly Guid ClsidProfileMgr = new("33C53A50-F456-4884-B049-85FD643ECFED");
    private static readonly Guid CategoryKeyboard = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    private static readonly Guid T9Clsid = new("A7E91C20-4B3D-4F18-9C2A-1B8E6D0A1001");
    private static readonly Guid T9Profile = new("A7E91C20-4B3D-4F18-9C2A-1B8E6D0A1002");
    private const ushort LangZhCn = 0x0804;

    // TF_IPPMF_FORPROCESS | TF_IPPMF_FORSESSION | TF_IPPMF_ENABLEPROFILE
    private const uint ActivateFlags = 0x20000000 | 0x00000001 | 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct TfInputProcessorProfile
    {
        public uint ProfileType;
        public ushort Langid;
        public Guid Clsid;
        public Guid Profile;
        public Guid Category;
        public IntPtr Substitute;
        public uint Capabilities;
        public IntPtr Hkl;
        public uint Flags;
    }

    [ComImport]
    [Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        [PreserveSig]
        int ActivateProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);

        [PreserveSig]
        int DeactivateProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);

        [PreserveSig]
        int GetProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, out TfInputProcessorProfile info);

        [PreserveSig]
        int EnumProfiles(ushort langid, out IntPtr enumerator);

        [PreserveSig]
        int ReleaseInputProcessor(ref Guid clsid, uint flags);

        [PreserveSig]
        int RegisterProfile(
            ref Guid clsid,
            ushort langid,
            ref Guid profile,
            [MarshalAs(UnmanagedType.LPWStr)] string description,
            uint descriptionLength,
            [MarshalAs(UnmanagedType.LPWStr)] string iconFile,
            uint iconFileLength,
            uint iconIndex,
            IntPtr substitute,
            uint preferredLayout,
            int enabledByDefault,
            uint flags);

        [PreserveSig]
        int UnregisterProfile(ref Guid clsid, ushort langid, ref Guid profile, uint flags);

        [PreserveSig]
        int GetActiveProfile(ref Guid category, out TfInputProcessorProfile profile);
    }

    /// <summary>当前活动输入法不是 T9 时激活它；已是 T9 或任何异常都安全返回。</summary>
    public static void EnsureT9Active()
    {
        // TSF 配置管理器要求 STA；放独立 STA 线程，避免依赖调用方套间，也不阻塞 UI 启动。
        var thread = new Thread(ActivateOnStaThread) { Name = "T9.ProfileActivate", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(4)))
        {
            Log.Warn("开机激活 T9 超时（不影响使用，可手动 Win+空格 切换）");
        }
    }

    private static void ActivateOnStaThread()
    {
        try
        {
            var managerType = Type.GetTypeFromCLSID(ClsidProfileMgr);
            if (managerType is null)
            {
                Log.Warn("开机激活 T9：无法创建 ITfInputProcessorProfileMgr COM 对象");
                return;
            }

            var manager = (ITfInputProcessorProfileMgr)Activator.CreateInstance(managerType)!;
            var category = CategoryKeyboard;
            var getHr = manager.GetActiveProfile(ref category, out var active);
            if (getHr == 0 && active.Clsid == T9Clsid)
            {
                Log.Info("当前输入法已是 T9 九键，跳过开机激活");
                return;
            }

            var clsid = T9Clsid;
            var profile = T9Profile;
            var hr = manager.ActivateProfile(
                1, LangZhCn, ref clsid, ref profile, IntPtr.Zero, ActivateFlags);
            Log.Info($"开机激活 T9 输入法 hr=0x{hr:X8}，原活动 clsid={active.Clsid}");
        }
        catch (Exception ex)
        {
            Log.Warn($"开机激活 T9 失败（不影响使用，可手动 Win+空格 切换）: {ex.Message}");
        }
    }
}
