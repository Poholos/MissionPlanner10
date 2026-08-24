using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class SystemAwakeServiceTests {
  [Fact]
  public void LinuxCommandUsesArgumentListWithoutShellText() {
    AwakeCommand? command = SystemAwakeService.BuildLinuxCommand("/bin/true", "/bin/true");

    Assert.NotNull(command);
    Assert.Equal("/bin/true", command!.FileName);
    Assert.Contains("--what=sleep", command.Arguments);
    Assert.Contains("--", command.Arguments);
    Assert.DoesNotContain(command.Arguments, argument => argument.Contains("sh -c"));
  }

  [Fact]
  public void LinuxCommandRequiresBothExecutables() {
    Assert.Null(SystemAwakeService.BuildLinuxCommand("/missing/inhibit", "/bin/true"));
    Assert.Null(SystemAwakeService.BuildLinuxCommand("/bin/true", "/missing/tail"));
  }

  [Fact]
  public void MacCommandTracksOnlyCurrentApplicationProcess() {
    AwakeCommand command = SystemAwakeService.BuildMacCommand("/usr/bin/caffeinate", 12345);

    Assert.Equal(new[] { "-i", "-w", "12345" }, command.Arguments);
  }
}
