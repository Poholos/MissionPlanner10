using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class OperationControllerTests {
  [Fact]
  public void Exclusive_controller_rejects_overlap_and_accepts_after_completion() {
    using var controller = new ExclusiveOperationController();
    ExclusiveOperationController.Lease first = Assert.IsType<ExclusiveOperationController.Lease>(
        controller.TryBegin());

    Assert.True(controller.IsRunning);
    Assert.Null(controller.TryBegin());

    first.Dispose();

    Assert.False(controller.IsRunning);
    using ExclusiveOperationController.Lease second =
        Assert.IsType<ExclusiveOperationController.Lease>(controller.TryBegin());
    Assert.True(controller.IsRunning);
  }

  [Fact]
  public void Completed_lease_cannot_clear_or_dispose_a_newer_operation() {
    using var controller = new ExclusiveOperationController();
    ExclusiveOperationController.Lease first = Assert.IsType<ExclusiveOperationController.Lease>(
        controller.TryBegin());
    first.Dispose();
    using ExclusiveOperationController.Lease second =
        Assert.IsType<ExclusiveOperationController.Lease>(controller.TryBegin());

    first.Dispose();
    controller.CancelCurrent();

    Assert.True(controller.IsRunning);
    Assert.True(second.Token.IsCancellationRequested);
  }

  [Fact]
  public void Disposing_controller_cancels_active_operation_and_blocks_restart() {
    var controller = new ExclusiveOperationController();
    using ExclusiveOperationController.Lease operation =
        Assert.IsType<ExclusiveOperationController.Lease>(controller.TryBegin());

    controller.Dispose();
    controller.Dispose();

    Assert.True(operation.Token.IsCancellationRequested);
    Assert.Throws<ObjectDisposedException>(() => controller.TryBegin());
  }
}
