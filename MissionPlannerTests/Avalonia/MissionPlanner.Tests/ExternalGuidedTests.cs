using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MissionPlanner.Comms;
using MissionPlanner.Services;
using MissionPlanner.Utilities;
using MissionPlanner.ViewModels;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;
using MissionPlanner.Views;

namespace MissionPlanner.Tests;

public sealed class ExternalGuidedTests {
  [Theory]
  [InlineData("0,0,25", 0, 0, 25)]
  [InlineData(" 34.1234567, 33.7654321, 50.5\n", 34.1234567, 33.7654321, 50.5)]
  [InlineData("-90,-180,10000", -90, -180, 10000)]
  public void Parses_the_official_invariant_lat_lon_alt_contract(
      string text, double latitude, double longitude, double altitude) {
    Assert.True(ExternalGuidedViewModel.TryParseWaypoint(
        text, out ExternalGuidedWaypoint waypoint, out string error), error);
    Assert.Equal(latitude, waypoint.Latitude, 7);
    Assert.Equal(longitude, waypoint.Longitude, 7);
    Assert.Equal(altitude, waypoint.RelativeAltitudeM, 7);
  }

  [Theory]
  [InlineData("")]
  [InlineData("1,2")]
  [InlineData("1,2,3,4")]
  [InlineData("NaN,2,3")]
  [InlineData("91,2,3")]
  [InlineData("1,181,3")]
  [InlineData("1,2,0")]
  [InlineData("1,2,10001")]
  [InlineData("1.5,2.5,3,5")]
  public void Rejects_ambiguous_or_unsafe_target_files(string text) {
    Assert.False(ExternalGuidedViewModel.TryParseWaypoint(
        text, out _, out string error));
    Assert.NotEmpty(error);
  }

  [AvaloniaFact]
  public async Task Requires_confirmation_and_sends_only_to_the_bound_target() {
    string root = Path.Combine(Path.GetTempPath(), "mp-external-guided-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    string path = Path.Combine(root, "target.txt");
    await File.WriteAllTextAsync(path, "12.5,23.5,45", Encoding.UTF8);
    try {
      MAVLinkInterface firstLink = OpenLink();
      MAVLinkInterface secondLink = OpenLink();
      NmeaVehicleTarget? current = new(firstLink, 42, 7);
      int confirmations = 0;
      int commands = 0;
      bool? firstSetGuided = null;
      Locationwp? last = null;
      using var viewModel = new ExternalGuidedViewModel(
          () => current,
          (target, waypoint, setGuided) => {
            Assert.Same(firstLink, target.Link);
            firstSetGuided ??= setGuided;
            last = waypoint;
            Interlocked.Increment(ref commands);
          },
          (_, _) => {
            confirmations++;
            return Task.FromResult(true);
          },
          (_, _) => Task.FromResult("12.5,23.5,45"));
      viewModel.FilePath = path;

      await viewModel.ToggleCommand.ExecuteAsync(null);
      await WaitUntilAsync(() => Volatile.Read(ref commands) > 0);

      Assert.Equal(1, confirmations);
      Assert.True(viewModel.IsRunning);
      Assert.True(firstSetGuided);
      Assert.NotNull(last);
      Assert.Equal(12.5, last.Value.lat, 7);
      Assert.Equal(23.5, last.Value.lng, 7);
      Assert.Equal(45, last.Value.alt, 7);

      current = new NmeaVehicleTarget(secondLink, 42, 7);
      viewModel.SynchronizeActiveTarget();
      await WaitUntilAsync(() => !viewModel.IsRunning
          && viewModel.Status.Contains("active modem or vehicle changed",
              StringComparison.OrdinalIgnoreCase));
      int stoppedAt = Volatile.Read(ref commands);
      await Task.Delay(1100);
      Assert.Equal(stoppedAt, Volatile.Read(ref commands));
    } finally {
      Directory.Delete(root, recursive: true);
    }
  }

  [AvaloniaFact]
  public async Task Rejecting_confirmation_never_sends_a_guided_command() {
    string path = Path.GetTempFileName();
    await File.WriteAllTextAsync(path, "1,2,30");
    try {
      MAVLinkInterface link = OpenLink();
      int commands = 0;
      using var viewModel = new ExternalGuidedViewModel(
          () => new NmeaVehicleTarget(link, 1, 1),
          (_, _, _) => Interlocked.Increment(ref commands),
          (_, _) => Task.FromResult(false),
          (_, _) => Task.FromResult("1,2,30"));
      viewModel.FilePath = path;

      await viewModel.ToggleCommand.ExecuteAsync(null);

      Assert.False(viewModel.IsRunning);
      Assert.Equal(0, commands);
      Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    } finally {
      File.Delete(path);
    }
  }

  [AvaloniaFact]
  public void Native_view_and_advanced_tools_expose_external_guided() {
    using var viewModel = new ExternalGuidedViewModel();
    var view = new ExternalGuidedView { DataContext = viewModel };
    var advanced = new ConfigAdvancedViewModel();

    Assert.NotNull(view.FindControl<Button>("ToggleExternalGuidedButton"));
    Assert.Contains(advanced.Actions, action => action.Label == "External Guided");
  }

  private static MAVLinkInterface OpenLink() {
    var link = new MAVLinkInterface {
      BaseStream = new OpenSerial(),
    };
    return link;
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    for (int attempt = 0; attempt < 250; attempt++) {
      Dispatcher.UIThread.RunJobs();
      if (condition()) {
        return;
      }
      await Task.Delay(10);
    }
    Assert.Fail("Condition was not reached before the test timeout.");
  }

  private sealed class OpenSerial : ICommsSerial {
    private int _open = 1;
    public Stream BaseStream { get; } = new MemoryStream();
    public int BaudRate { get; set; }
    public int BytesToRead => 0;
    public int BytesToWrite => 0;
    public int DataBits { get; set; } = 8;
    public bool DtrEnable { get; set; }
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    public string PortName { get; set; } = "EXTERNAL-GUIDED-TEST";
    public int ReadBufferSize { get; set; }
    public int ReadTimeout { get; set; }
    public bool RtsEnable { get; set; }
    public int WriteBufferSize { get; set; }
    public int WriteTimeout { get; set; }
    public void Open() => Volatile.Write(ref _open, 1);
    public void Close() => Volatile.Write(ref _open, 0);
    public void Dispose() {
      Close();
      BaseStream.Dispose();
    }
    public void DiscardInBuffer() { }
    public int Read(byte[] buffer, int offset, int count) => 0;
    public int ReadByte() => -1;
    public int ReadChar() => -1;
    public string ReadExisting() => "";
    public string ReadLine() => "";
    public void Write(string text) { }
    public void Write(byte[] buffer, int offset, int count) { }
    public void WriteLine(string text) { }
    public void toggleDTR() { }
  }
}
