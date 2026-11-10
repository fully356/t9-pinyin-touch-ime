using System.Speech.Recognition;

namespace T9Pane.Services;

/// <summary>
/// 离线语音输入，用 Windows 自带的中文识别引擎（无需联网、不依赖 Ollama）。
/// 按住麦克风键开始，松手停止；识别结果通过 <see cref="Recognized"/> 回抛，
/// 由调用方走 SendText() 上屏 —— 与九宫格同一条通道，不碰 TSF 组合范围。
/// </summary>
internal sealed class VoiceInput : IDisposable
{
    private readonly object _gate = new();
    private SpeechRecognitionEngine? _engine;
    private bool _unavailable;

    public bool IsListening { get; private set; }

    /// <summary>注意：在识别线程上触发，调用方需自行切回 UI 线程。</summary>
    public event Action<string>? Recognized;

    /// <summary>失败时给出可读原因，供界面提示。</summary>
    public string LastError { get; private set; } = "";

    public bool Start()
    {
        lock (_gate)
        {
            if (IsListening)
            {
                return true;
            }

            if (_unavailable)
            {
                return false;
            }

            try
            {
                if (_engine is null && !CreateEngine())
                {
                    return false;
                }

                _engine!.RecognizeAsync(RecognizeMode.Multiple);
                IsListening = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warn($"启动语音输入失败: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>松手：让当前这句识别完再停，不要 Cancel —— Cancel 会把刚说的话丢掉。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!IsListening)
            {
                return;
            }

            IsListening = false;
            try
            {
                _engine?.RecognizeAsyncStop();
            }
            catch (Exception ex)
            {
                Log.Warn($"停止语音输入失败: {ex.Message}");
            }
        }
    }

    private bool CreateEngine()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0)
        {
            _unavailable = true;
            LastError = "系统未安装任何语音识别引擎";
            Log.Warn($"语音输入不可用：{LastError}");
            return false;
        }

        // 优先中文。台式机版与 OneCore 版都在列表里，取第一个匹配项即可。
        var info = installed.FirstOrDefault(r =>
                       r.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                   ?? installed[0];

        _engine = new SpeechRecognitionEngine(info);
        _engine.LoadGrammar(new DictationGrammar());
        _engine.SetInputToDefaultAudioDevice();
        _engine.SpeechRecognized += (_, e) =>
        {
            var text = e.Result?.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                Recognized?.Invoke(text.Trim());
            }
        };
        _engine.RecognizeCompleted += (_, _) => IsListening = false;
        Log.Info($"语音输入就绪：{info.Name}（{info.Culture.Name}）");
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _engine?.RecognizeAsyncCancel();
                _engine?.Dispose();
            }
            catch
            {
                // 退出路径，忽略
            }

            _engine = null;
            IsListening = false;
        }
    }
}
