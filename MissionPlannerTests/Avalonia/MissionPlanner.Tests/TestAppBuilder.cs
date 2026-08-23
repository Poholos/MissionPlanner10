using Avalonia;
using Avalonia.Headless;
using MissionPlanner;
using MissionPlanner.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MissionPlanner.Tests;

public static class TestAppBuilder {
  public static AppBuilder BuildAvaloniaApp() =>
      AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
