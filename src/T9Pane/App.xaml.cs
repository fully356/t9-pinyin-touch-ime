using System.Threading;
using System.Windows;
using T9Pane.Native;
using T9Pane.Overlay;
using T9Pane.Services;
using Application = System.Windows.Application;

namespace T9Pane;

public partial class App
{
    private Mutex? _mutex;
    private AppSettings _settings = new();
    private T9Engine _engine = new();
    private UserLearning? _learning;
    private readonly ImeCatalog _catalog = new();
    private ForegroundTracker? _foreground;
    private PointerIntentTracker? _pointerIntent;
    private ChromiumAccessibilityActivator? _chromiumA11y;
    private T9OverlayWindow? _overlay;
    private KeyboardSession? _session;
    private TrayHost? _tray;
    private TsfProfileActivationSink? _profileSink;
    private readonly WindowFitter _fitter = new();

    /// <summary>
    /// 0=空闲，1=只跟线，2=全量同步。连发时只升不降：焦点事件必须盖过布局事件。
    /// </summary>
    private int _syncQueued;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = false;

        if (e.Args.Any(a => a.Equals("/register", StringComparison.OrdinalIgnoreCase)))
        {
            ImeRegister.Run(true);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a.Equals("/unregister", StringComparison.OrdinalIgnoreCase)))
        {
            ImeRegister.Run(false);
            Shutdown();
            return;
        }

        if (UiAccessInstall.TryHandoffToInstalled())
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\T9Pane.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        if (_settings.AutoStart)
        {
            _settings.Save();
        }
        ImeRegister.PointToNewestDll();
        ImeRegister.RepairProfileEnablement();
        // 重新登录后活动输入法会回到微软拼音，后端随开机自启时在这里把 T9 激活回来，
        // 避免重启后键盘不弹；内部已是 T9 则跳过，运行中手动切走不会被抢回。
        InputProfileActivator.EnsureT9Active();
        _settings.OutputMode = OutputMode.BuiltInT9;
        _catalog.Refresh();
        _engine.Load(_catalog, _settings.ExtraLexiconDirectories);
        _learning = UserLearning.Load(AppSettings.LearningPath);
        _engine.AttachLearning(_learning);
        _foreground = new ForegroundTracker(work => Dispatcher.BeginInvoke(work));
        _chromiumA11y = new ChromiumAccessibilityActivator();
        _overlay = new T9OverlayWindow(_engine, _settings, _foreground);
        _session = new KeyboardSession(_settings, _overlay, _foreground);
        _pointerIntent = new PointerIntentTracker(work => Dispatcher.BeginInvoke(work));
        _pointerIntent.TouchContact += () => _session?.NoteTouchModality();
        _pointerIntent.PointerUp += () => _session?.NotePointerUp();
        _pointerIntent.ContextMenu += () => _session?.NoteContextMenu();
        _pointerIntent.PointerDown += (x, y, target, targetPid, origin) =>
        {
            if (!PointerIntentTrackingPolicy.IsKeyboardWindow(
                    NativeMethods.GetWindowClass(target)))
            {
                var targetProcess = ShellProcess.Name(targetPid);
                if (origin == PointerInvocationOrigin.Unknown
                    && targetProcess == "startmenuexperiencehost")
                {
                    origin = PointerInvocationOrigin.StartMenuSurface;
                }
                if (origin != PointerInvocationOrigin.Unknown
                    || targetProcess is "startmenuexperiencehost"
                        or "searchhost")
                {
                    Log.Info(
                        $"系统指针按下 pid={targetPid} process={targetProcess} "
                        + $"origin={origin} x={x} y={y}");
                }
                _session?.NotePointerInput(x, y, origin);
            }
        };
        // 先挂 Changed，再 Start：启动时读到的语言栏才能立刻 Sync 握「显示触摸键盘」。
        ImeHost.Shared.Changed += () =>
        {
            var incoming = ImeHost.Shared.LastChangeIsLayoutOnly ? 1 : 2;
            while (true)
            {
                var current = Volatile.Read(ref _syncQueued);
                var next = incoming == 2 || current == 2 ? 2 : 1;
                if (Interlocked.CompareExchange(ref _syncQueued, next, current) != current)
                {
                    continue;
                }

                if (current == 0)
                {
                    Dispatcher.BeginInvoke(FlushImeSync);
                }

                return;
            }
        };
        ImeHost.Shared.Start();
        _profileSink = new TsfProfileActivationSink(ImeHost.Shared.NoteThreadProfile);
        _profileSink.Start();
        // 这里必须逐条通知立即同步：开始菜单搜索框由 StartMenuExperienceHost 转交给
        // SearchHost，授权发生在交接完成之前，靠随后每一条 TSF 通知补一次同步才能把
        // 键盘放上去。合并或降低优先级会让第一次点击弹不出来。
        _foreground.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            UpdatePointerTracking();
            _pointerIntent?.RefreshShellTargets();
            _chromiumA11y?.NoteForeground(_foreground.LastTarget);
            _session?.NoteFocusSettled();
        });
        _tray = new TrayHost(
            _settings,
            () => Sync(),
            ReloadLexicon,
            ExitApp,
            () => AppearanceDialog.ShowOpacity(_overlay, _settings),
            () => AppearanceDialog.ShowSkin(_overlay, _settings),
            () => AppearanceDialog.ShowScale(_overlay, _settings));

        Log.Info($"T9 九键输入法后端启动，{_catalog.Summary}，词库 {_engine.Count} 条");
        UpdatePointerTracking();
        Sync();
    }

    private void Sync() => _session?.Sync();

    private void FlushImeSync()
    {
        var kind = Interlocked.Exchange(ref _syncQueued, 0);
        if (kind == 0 || _session is null)
        {
            return;
        }

        UpdatePointerTracking();
        RescanChromiumA11y();
        _session.NoteImeChanged();
        _session.Sync(ImeHost.Shared.HasDocumentFocus, layoutOnly: kind == 1);
    }

    private long _a11yRescanTicks;

    /// <summary>
    /// 补扫 Chromium/Electron 宿主的无障碍握手。
    ///
    /// ChromiumAccessibilityActivator.NoteForeground 只在「前台窗口变化」时被调用，
    /// 而实测微信 4.x 里**切换聊天不会换前台窗口**，只会在同一个宿主里新建渲染子窗口 ——
    /// 新窗口永远等不到握手，UIA 就读不到聊天输入框，
    /// 表现为「微信里唤醒不了输入法」。这里每秒最多补扫一次；
    /// 握手内部按 hwnd 去重，已应答过的不会重发。
    /// </summary>
    private void RescanChromiumA11y()
    {
        if (_chromiumA11y is null || _foreground is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _a11yRescanTicks);
        if (now - last < 1000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _a11yRescanTicks, now, last) != last)
        {
            return;
        }

        _chromiumA11y.NoteForeground(_foreground.LastTarget);
    }

    private void UpdatePointerTracking() =>
        _pointerIntent?.SetEnabled(PointerIntentTrackingPolicy.ShouldEnableForSession(
            ImeHost.Shared.CanCommitForeground(),
            ImeHost.Shared.HasForegroundProfileLease(),
            ImeHost.Shared.HasSystemProfileLease(),
            ImeHost.Shared.HasOfficialT9Profile()));

    private void ReloadLexicon()
    {
        _catalog.Refresh();
        _engine.Load(_catalog, _settings.ExtraLexiconDirectories);
        // 重载主词库后重新挂载学习实例，保留内存里尚未落盘的学习数据。
        _engine.AttachLearning(_learning);
        PhraseBook.Reload();
        _overlay?.RefreshChrome();
        Log.Info($"已重新加载词库：{_engine.SourceDescription}，常用短语 {PhraseBook.Count} 条");
    }

    private void ExitApp()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pointerIntent?.Dispose();
        _chromiumA11y?.Dispose();
        _profileSink?.Dispose();
        ImeHost.Shared.Dispose();
        _overlay?.HideOverlay();
        _fitter.Restore();
        _foreground?.Dispose();
        _tray?.Dispose();
        _learning?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
