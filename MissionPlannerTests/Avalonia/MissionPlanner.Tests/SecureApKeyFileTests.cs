using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public class SecureApKeyFileTests {
  [Theory]
  [InlineData("flight-key")]
  [InlineData("flight-key.PEM")]
  [InlineData("flight.pem.backup")]
  public void Companion_paths_are_distinct_for_every_selected_extension(string fileName) {
    string selected = Path.Combine(Path.GetTempPath(), fileName);

    var paths = ConfigSecureApViewModel.KeyOutputPaths(selected);

    Assert.Equal(Path.GetFullPath(selected), paths.PemPath);
    Assert.Equal(3, new[] { paths.PemPath, paths.PrivateDataPath, paths.PublicDataPath }
        .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    string stem = Path.GetFileNameWithoutExtension(selected);
    Assert.EndsWith(stem + "_private_key.dat", paths.PrivateDataPath, StringComparison.Ordinal);
    Assert.EndsWith(stem + "_public_key.dat", paths.PublicDataPath, StringComparison.Ordinal);
  }

  [Fact]
  public void Generated_private_files_are_not_overwritten_and_are_owner_only_on_unix() {
    string directory = Path.Combine(
        Path.GetTempPath(), "mp-secure-key-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      string selected = Path.Combine(directory, "flight-key.PEM");
      var paths = ConfigSecureApViewModel.KeyOutputPaths(selected);
      var viewModel = new ConfigSecureApViewModel();

      viewModel.GenerateKey(selected);

      Assert.Contains("PRIVATE KEY", File.ReadAllText(paths.PemPath), StringComparison.Ordinal);
      Assert.StartsWith("PRIVATE_KEYV1:", File.ReadAllText(paths.PrivateDataPath),
          StringComparison.Ordinal);
      Assert.StartsWith("PUBLIC_KEYV1:", File.ReadAllText(paths.PublicDataPath),
          StringComparison.Ordinal);
      if (!OperatingSystem.IsWindows()) {
        UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, File.GetUnixFileMode(paths.PemPath));
        Assert.Equal(expected, File.GetUnixFileMode(paths.PrivateDataPath));
      }
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }
}
