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

    AssertPayload(mainFeature, wix, "MissionPlannerExecutable", "MissionPlanner.exe");
    AssertPayload(mainFeature, wix, "SqliteNativeFile", "e_sqlite3.dll");
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
