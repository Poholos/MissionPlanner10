using Avalonia.Headless.XUnit;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class ProgressReporterTests {
  [AvaloniaFact]
  public void Closing_progress_window_cancels_work_and_keeps_captured_token_readable() {
    var reporter = new ProgressReporter("Test operation");
    CancellationToken token = reporter.Token;
    reporter.Show();

    reporter.Close();

    Assert.True(token.IsCancellationRequested);
    Assert.True(reporter.CancelRequested);
    Assert.Equal(token, reporter.Token);
  }
}
