using System;
using System.IO;
using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;

namespace MissionPlanner;

sealed class Program {
  [STAThread]
  public static void Main(string[] args) {

    if (args.Length == 3 && args[0] == "--mcp-stdio" && args[1] == "--port" && int.TryParse(args[2], out int mcpPort)) {
      try { Environment.ExitCode = Services.Mcp.McpStdioBridge.RunAsync(mcpPort, Console.In, Console.Out).GetAwaiter().GetResult(); }
      catch (Exception e) { Console.Error.WriteLine("Mission Planner MCP connection failed: " + e.GetType().Name); Environment.ExitCode = 1; }
      return;
    }

    if (args.Length == 2 && args[0] == "--mcp-terminal-session") {
      try { Environment.ExitCode = Services.Mcp.McpTerminalLaunch.RunHandoffAsync(args[1]).GetAwaiter().GetResult(); }
      catch (Exception e) { Console.Error.WriteLine("Mission Planner agent launch failed: " + e.GetType().Name); Environment.ExitCode = 1; }
      return;
    }

    Services.AppPaths.Initialize();

    AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => {
      LogCrash(e.Exception);
      e.SetObserved();
    };

    IconProvider.Current.Register<FontAwesomeIconProvider>();
    try {
      BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    } catch (Exception ex) {
      LogCrash(ex);
      throw;
    }
  }

  private static void LogCrash(Exception? ex) {
    if (ex == null) {
      return;
    }
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(Services.AppPaths.CrashLogPath)!);
      File.AppendAllText(Services.AppPaths.CrashLogPath, $"---- crash ----\n{ex}\n\n");
    } catch {

    }
  }

  public static AppBuilder BuildAvaloniaApp() =>
      AppBuilder.Configure<App>()
          .UsePlatformDetect()
          // Embed popups in the top-level window instead of creating separate X11 override-redirect
          // windows. Avalonia's X11 ManagedPopupPositioner mislocates ComboBox dropdowns under HiDPI
          // + GNOME fractional scaling (e.g. the FlightData "Set Mode" list flipping over the HUD or
          // jumping to the top-left corner). Overlay popups bypass that positioner entirely.
          .With(new X11PlatformOptions { OverlayPopups = true })
          .WithInterFont()
          .LogToTrace();
}
