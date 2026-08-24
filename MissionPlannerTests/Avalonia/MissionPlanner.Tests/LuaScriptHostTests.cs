using System.Collections.Concurrent;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class LuaScriptHostTests {
  [Fact]
  public async Task Abort_stops_cooperative_lua_loop() {
    using var host = new LuaScriptHost();
    var output = new ConcurrentQueue<string>();
    host.Output += output.Enqueue;

    Task run = host.RunAsync(
        "print('loop-started')\nwhile not mp_should_abort() do end\nmp_check_abort()");
    Assert.True(SpinWait.SpinUntil(
        () => output.Contains("loop-started"), TimeSpan.FromSeconds(2)));

    host.Abort();
    await run.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(host.IsRunning);
    Assert.Contains("Script aborted.", output);
  }

  [Fact]
  public async Task Dispose_cancels_active_script_and_rejects_restart() {
    var host = new LuaScriptHost();
    var output = new ConcurrentQueue<string>();
    host.Output += output.Enqueue;
    Task run = host.RunAsync(
        "print('loop-started')\nwhile not mp_should_abort() do end\nmp_check_abort()");
    Assert.True(SpinWait.SpinUntil(
        () => output.Contains("loop-started"), TimeSpan.FromSeconds(2)));

    host.Dispose();
    await run.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(host.IsRunning);
    await Assert.ThrowsAsync<ObjectDisposedException>(() => host.RunAsync("return 1"));
  }
}
