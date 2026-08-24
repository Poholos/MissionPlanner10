using Avalonia.Headless.XUnit;
using MissionPlanner.Comms;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public class CompassMotLifecycleTests {
  [AvaloniaFact]
  public void Closing_idle_page_does_not_send_unsolicited_calibration_ack() {
    using var stream = new CommsInjection();
    using var link = new MAVLinkInterface { BaseStream = stream };
    int writes = 0;
    stream.WriteCallback += (_, _) => Interlocked.Increment(ref writes);
    var viewModel = new ConfigCompassMotViewModel(link);

    viewModel.Dispose();
    viewModel.Dispose();

    Assert.Equal(0, Volatile.Read(ref writes));
  }
}
