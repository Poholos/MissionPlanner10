using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public class SecurityQualityTests {
  [Theory]
  [InlineData(128)]
  [InlineData(256)]
  public void WinZipAes_roundtrip_supports_both_key_sizes(int keySize) {
    byte[] payload = Encoding.UTF8.GetBytes("authenticated Mission Planner archive");
    using var archive = new MemoryStream();
    using (var output = new ZipOutputStream(archive) {
      IsStreamOwner = false,
      Password = "test-only-password",
    }) {
      output.PutNextEntry(new ZipEntry("payload.txt") { AESKeySize = keySize });
      output.Write(payload, 0, payload.Length);
      output.CloseEntry();
      output.Finish();
    }

    archive.Position = 0;
    using var zip = new ZipFile(archive) {
      IsStreamOwner = false,
      Password = "test-only-password",
    };
    ZipEntry entry = Assert.IsType<ZipEntry>(zip.GetEntry("payload.txt"));
    using Stream input = zip.GetInputStream(entry);
    using var restored = new MemoryStream();
    input.CopyTo(restored);

    Assert.Equal(payload, restored.ToArray());
  }

  [Fact]
  public void Parameter_export_keeps_one_invariant_line_per_value() {
    var values = new System.Collections.Hashtable {
      ["INTEGER"] = 42,
      ["FRACTION"] = 12.5,
    };
    string path = Path.Combine(Path.GetTempPath(), $"mp-param-{Guid.NewGuid():N}.param");
    try {
      ParamFile.SaveParamFile(path, values);
      string[] lines = File.ReadAllLines(path);

      Assert.Equal(["FRACTION,12.5", "INTEGER,42"], lines);
    } finally {
      if (File.Exists(path)) {
        File.Delete(path);
      }
    }
  }
}
