using System.Runtime.Serialization;
using System.Security.Cryptography;
using MissionPlanner.Mavlink;
using MissionPlanner.Utilities;

namespace MissionPlanner.Tests;

public sealed class MavAuthKeyStoreTests : IDisposable {
  private readonly string _directory =
      Path.Combine(Path.GetTempPath(), "mp-authkeys-" + Guid.NewGuid().ToString("N"));

  public MavAuthKeyStoreTests() => Directory.CreateDirectory(_directory);

  [Fact]
  public void New_store_round_trips_keys_with_persisted_material() {
    string keyFile = Path.Combine(_directory, "authkeys.xml");
    string materialFile = Path.Combine(_directory, "authkeys.key");
    var keys = Keys(("alpha", 17));

    using (var store = new MavAuthKeyStore(keyFile, materialFile, NoLegacyCandidates)) {
      Assert.Empty(store.Load());
      store.Save(keys);
    }

    Assert.True(File.Exists(keyFile));
    Assert.True(File.Exists(materialFile));
    using var reopened = new MavAuthKeyStore(keyFile, materialFile, NoLegacyCandidates);
    MAVAuthKeys.AuthKeys loaded = reopened.Load();
    Assert.Equal(keys["alpha"].Key, loaded["alpha"].Key);
  }

  [Fact]
  public void Legacy_mac_encrypted_store_is_migrated_without_changing_its_keys() {
    string keyFile = Path.Combine(_directory, "authkeys.xml");
    string materialFile = Path.Combine(_directory, "authkeys.key");
    byte[] legacyKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    byte[] legacyIv = Enumerable.Range(101, 16).Select(value => (byte)value).ToArray();
    var expected = Keys(("legacy", 42));
    WriteEncrypted(keyFile, expected, legacyKey, legacyIv);

    using (var store = new MavAuthKeyStore(
               keyFile, materialFile,
               () => [new Crypto(legacyKey, legacyIv)])) {
      MAVAuthKeys.AuthKeys loaded = store.Load();
      Assert.Equal(expected["legacy"].Key, loaded["legacy"].Key);
    }

    Assert.True(File.Exists(materialFile));
    using var reopened = new MavAuthKeyStore(keyFile, materialFile, NoLegacyCandidates);
    Assert.Equal(expected["legacy"].Key, reopened.Load()["legacy"].Key);
  }

  [Fact]
  public void Unreadable_existing_store_is_preserved_and_cannot_be_overwritten() {
    string keyFile = Path.Combine(_directory, "authkeys.xml");
    string materialFile = Path.Combine(_directory, "authkeys.key");
    byte[] original = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
    File.WriteAllBytes(keyFile, original);

    using var store = new MavAuthKeyStore(keyFile, materialFile, NoLegacyCandidates);
    InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.Load());

    Assert.Contains("left unchanged", error.Message);
    Assert.Equal(original, File.ReadAllBytes(keyFile));
    Assert.Throws<InvalidOperationException>(() => store.Save(Keys(("new", 1))));
    Assert.Equal(original, File.ReadAllBytes(keyFile));
  }

  [Fact]
  public void Replacing_a_store_keeps_a_readable_backup() {
    string keyFile = Path.Combine(_directory, "authkeys.xml");
    string materialFile = Path.Combine(_directory, "authkeys.key");
    using (var store = new MavAuthKeyStore(keyFile, materialFile, NoLegacyCandidates)) {
      store.Load();
      store.Save(Keys(("first", 1)));
      store.Save(Keys(("first", 1), ("second", 2)));
      store.Save(Keys(("first", 1), ("second", 2), ("third", 3)));
    }

    string backup = keyFile + ".bak";
    Assert.True(File.Exists(backup));
    using var backupStore = new MavAuthKeyStore(backup, materialFile, NoLegacyCandidates);
    MAVAuthKeys.AuthKeys loaded = backupStore.Load();
    Assert.True(loaded.ContainsKey("first"));
    Assert.True(loaded.ContainsKey("second"));
    Assert.False(loaded.ContainsKey("third"));
  }

  [Fact]
  public void Crypto_instances_do_not_mutate_the_legacy_defaults() {
    byte[] replacementKey = Enumerable.Repeat((byte)0x55, 32).ToArray();
    byte[] replacementIv = Enumerable.Repeat((byte)0x66, 16).ToArray();
    using (var changed = new Crypto()) {
      changed.SetBinaryKeys(replacementKey, replacementIv);
    }

    using var fresh = new Crypto();
    fresh.ExtractBinaryKeys(out byte[] freshKey, out byte[] freshIv);
    Assert.NotEqual(replacementKey, freshKey);
    Assert.NotEqual(replacementIv, freshIv);
  }

  private static IReadOnlyList<Crypto> NoLegacyCandidates() => Array.Empty<Crypto>();

  private static MAVAuthKeys.AuthKeys Keys(params (string Name, byte Fill)[] definitions) {
    var keys = new MAVAuthKeys.AuthKeys();
    foreach ((string name, byte fill) in definitions) {
      keys[name] = new MAVAuthKeys.AuthKey {
        Name = name,
        Key = Enumerable.Repeat(fill, 32).ToArray(),
      };
    }
    return keys;
  }

  private static void WriteEncrypted(string path, MAVAuthKeys.AuthKeys keys,
      byte[] key, byte[] iv) {
    var serializer = new DataContractSerializer(
        typeof(MAVAuthKeys.AuthKeys), [typeof(MAVAuthKeys.AuthKey)]);
    using var crypto = new Crypto(key, iv);
    using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using var encrypted = new CryptoStream(
        file, crypto.algorithm.CreateEncryptor(), CryptoStreamMode.Write);
    serializer.WriteObject(encrypted, keys);
  }

  public void Dispose() {
    try {
      Directory.Delete(_directory, recursive: true);
    } catch {
    }
  }
}
