using System.Reflection;
using System.Text.RegularExpressions;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class AppVersionTests {
  [Fact]
  public void Product_identity_is_MissionPlanner10_while_managed_ABI_remains_native() {
    Assert.Equal("Mission Planner 10", AppVersion.DisplayName);
    Assert.Equal("MissionPlanner10", AppVersion.ExecutableName);
    Assert.Equal("MissionPlanner", typeof(AppVersion).Assembly.GetName().Name);
  }

  [Fact]
  public void Built_application_uses_four_part_local_build_identity() {
    Assembly assembly = typeof(AppVersion).Assembly;
    string fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
    string informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    Version abiVersion = assembly.GetName().Version!;
    string[] fields = fileVersion.Split('.');

    Assert.Equal(4, fields.Length);
    Assert.All(fields, field => Assert.True(int.TryParse(field, out _)));
    Assert.True(int.Parse(fields[3]) > 0);
    Assert.Equal($"{fields[0]}.{fields[1]}.{fields[2]}.0", abiVersion.ToString());
    Assert.Matches(
        $@"^{Regex.Escape(fileVersion)}\+[0-9a-f]{{8}}(?:\.dirty)?$", informational);
  }

  [Theory]
  [InlineData("1.3.83+20260821.8a07b1b", "1.3.83", "2026-08-21", "8a07b1b",
      "1.3.83 (2026-08-21, 8a07b1b)")]
  [InlineData("1.3.83+20260821.8a07b1b.dirty", "1.3.83", "2026-08-21",
      "8a07b1b-dirty", "1.3.83 (2026-08-21, 8a07b1b-dirty)")]
  [InlineData("1.3.83+8A07B1B02FF8", "1.3.83", "", "8A07B1B0", "1.3.83 (8A07B1B0)")]
  [InlineData("1.3.83+20260821.8a07b1b02ff8b54dc72957154819a6f0f0e4d055",
      "1.3.83", "2026-08-21", "8a07b1b0", "1.3.83 (2026-08-21, 8a07b1b0)")]
  [InlineData("1.3.83.1+8a07b1b02ff8", "1.3.83.1", "", "8a07b1b0",
      "1.3.83.1 (8a07b1b0)")]
  [InlineData("1.3.83.1+8a07b1b0.dirty", "1.3.83.1", "", "8a07b1b0-dirty",
      "1.3.83.1 (8a07b1b0-dirty)")]
  [InlineData("1.3.83+20260821", "1.3.83", "2026-08-21", "",
      "1.3.83 (2026-08-21)")]
  [InlineData("1.3.83", "1.3.83", "", "", "1.3.83")]
  public void Composite_version_is_split_for_ui(string value, string number,
      string buildDate, string hash, string display) {
    AppVersionParts parsed = AppVersion.Parse(value);

    Assert.Equal(number, parsed.Number);
    Assert.Equal(buildDate, parsed.BuildDate);
    Assert.Equal(hash, parsed.Hash);
    Assert.Equal(display, parsed.Display);
  }

  [Theory]
  [InlineData("1.3.83+not-a-hash")]
  [InlineData("")]
  [InlineData(null)]
  public void Invalid_metadata_is_not_presented_as_a_commit(string? value) {
    AppVersionParts parsed = AppVersion.Parse(value);

    Assert.Empty(parsed.Hash);
  }
}
