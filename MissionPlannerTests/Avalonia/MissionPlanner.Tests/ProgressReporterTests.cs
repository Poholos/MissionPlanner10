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

  [AvaloniaFact]
  public void Completing_progress_window_does_not_cancel_successful_work() {
    var reporter = new ProgressReporter("Completed operation");
    CancellationToken token = reporter.Token;
    bool cancellationCallbackRan = false;
    using CancellationTokenRegistration registration = token.Register(
        () => cancellationCallbackRan = true);
    reporter.Show();

    reporter.Complete();

    Assert.False(token.IsCancellationRequested);
    Assert.False(reporter.CancelRequested);
    Assert.False(cancellationCallbackRan);
    Assert.Equal(token, reporter.Token);
  }

  [AvaloniaFact]
  public void Cancellation_action_can_describe_a_non_destructive_parameter_skip() {
    var reporter = new ProgressReporter("Parameter loading");

    reporter.SetCancellationText("Skip Parameters");

    Assert.Equal("Skip Parameters", reporter.CancellationText);
  }
}
