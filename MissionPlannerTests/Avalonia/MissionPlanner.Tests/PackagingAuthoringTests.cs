using System.Xml.Linq;

namespace MissionPlanner.Tests;

public class PackagingAuthoringTests {
  [Fact]
  public void Windows_msi_assigns_canonical_names_to_explicit_payload_files() {
    string root = FindRepoRoot();
    XDocument package = XDocument.Load(
        Path.Combine(root, "build", "windows", "msi", "Package.wxs"));
    XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
    XElement mainFeature = Assert.Single(package.Descendants(wix + "Feature"),
        element => (string?)element.Attribute("Id") == "Main");

    AssertPayload(mainFeature, wix, "MissionPlanner10Executable", "MissionPlanner10.exe");
    AssertPayload(mainFeature, wix, "SqliteNativeFile", "e_sqlite3.dll");
  }

  [Fact]
  public void Make_package_targets_select_their_platform_rid() {
    string makefile = File.ReadAllText(Path.Combine(FindRepoRoot(), "Makefile"))
        .Replace("\r\n", "\n", StringComparison.Ordinal);

    Assert.Contains("linux-packages linux-tar linux-deb: RID=linux-x64", makefile,
        StringComparison.Ordinal);
    Assert.Contains("windows-packages windows-zip windows-msi: RID=win-x64", makefile,
        StringComparison.Ordinal);
  }

  [Fact]
  public void Windows_driver_catalog_recognizes_both_Pixhawk6C_interfaces() {
    string inf = File.ReadAllText(Path.Combine(FindRepoRoot(), "Drivers", "Holybro.inf"));

    Assert.Equal(2, Count(inf, @"USB\VID_3162&PID_0053&MI_00"));
    Assert.Equal(2, Count(inf, @"USB\VID_3162&PID_0053&MI_02"));
    Assert.Contains("DESCRIPTION53=\"Pixhawk6C-MAVLink\"", inf, StringComparison.Ordinal);
    Assert.Contains("DESCRIPTION53SL=\"Pixhawk6C-SLCAN\"", inf, StringComparison.Ordinal);
  }

  private static int Count(string text, string value) {
    int count = 0;
    for (int index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
         index += value.Length) {
      count++;
    }
    return count;
  }

  private static void AssertPayload(
      XElement feature, XNamespace wix, string id, string expectedName) {
    XElement file = Assert.Single(feature.Descendants(wix + "File"),
        element => (string?)element.Attribute("Id") == id);
    Assert.Equal(expectedName, (string?)file.Attribute("Name"));
    Assert.EndsWith(expectedName, (string?)file.Attribute("Source"),
        StringComparison.Ordinal);
  }

  private static string FindRepoRoot() {
    string? path = AppContext.BaseDirectory;
    while (path != null && !File.Exists(Path.Combine(path, "MissionPlanner.slnx"))) {
      path = Directory.GetParent(path)?.FullName;
    }
    return path ?? throw new DirectoryNotFoundException("Repository root not found.");
  }
}
