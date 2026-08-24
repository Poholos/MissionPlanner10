using System.Text;
using System.Reflection;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public class SecurityQualityTests {
  [Theory]
  [InlineData(16, "9D7A33E35E0C9D71B336349EC3D3E0C60E800D7475D237305C5AECB938D22627C029111085A9C56B86706F374C33B56EF5868AC390")]
  [InlineData(32, "48854E53DEF92215AA2E8EB822E7EDB674E86911A336530E4CB3DDCD8DD7356FF066FA6EBB87AF797B2B17CDEEF937E52290A1AB61")]
  public void WinZipAes_transform_keeps_the_reference_ciphertext(
      int blockSize, string expectedCiphertext) {
    Type transformType = typeof(ZipFile).Assembly.GetType(
        "ICSharpCode.SharpZipLib.Encryption.ZipAESTransform", throwOnError: true)!;
    byte[] salt = Enumerable.Range(1, blockSize / 2).Select(value => (byte)value).ToArray();
    object instance = Activator.CreateInstance(
        transformType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args: ["reference-password", salt, blockSize, true],
        culture: null)!;
    using var transform = Assert.IsAssignableFrom<ICryptoTransform>(instance);
    byte[] input = Enumerable.Range(0, 53).Select(value => (byte)(value * 3 + 1)).ToArray();
    byte[] output = new byte[input.Length];

    Assert.Equal(input.Length, transform.TransformBlock(input, 0, input.Length, output, 0));
    Assert.Equal(expectedCiphertext, Convert.ToHexString(output));
  }

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
