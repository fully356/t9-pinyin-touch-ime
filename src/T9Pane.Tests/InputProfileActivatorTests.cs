using T9Pane.Services;

namespace T9Pane.Tests;

public class InputProfileActivatorTests
{
    [Fact]
    public void Ensure_active_never_throws_and_returns_quickly()
    {
        // 激活器内部在独立 STA 线程跑 TSF COM，任何失败都被吞掉只记日志。
        // 这里只验证调用本身不向调用方抛异常、且在超时窗口内返回（不阻塞启动）。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        InputProfileActivator.EnsureT9Active();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"激活调用耗时过长: {sw.Elapsed}");
    }
}
