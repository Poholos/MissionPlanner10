using System;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MissionPlanner.Utilities;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;

namespace MissionPlanner.ViewModels.GCSViews.ConfigurationView;

public partial class ConfigSecureApViewModel : ViewModelBase {
  private AsymmetricCipherKeyPair? _keyPair;

  [ObservableProperty]
  private string _publicKeyText = "";

  [ObservableProperty]
  private string _bootloaderPath = "";

  [ObservableProperty]
  private string _firmwarePath = "";

  [ObservableProperty]
  private string _log = "";

  public void GenerateKey(string pemSavePath) {
    try {
      _keyPair = SignedFW.GenerateKey();

      var textWriter = new StringWriter();
      PemWriter pemWriter = new PemWriter(textWriter);
      pemWriter.WriteObject(_keyPair);
      pemWriter.Writer.Flush();
      string privatekey = textWriter.ToString();
      (string pemPath, string privateDataPath, string publicDataPath) =
          KeyOutputPaths(pemSavePath);

      WritePrivateKeyFile(pemPath, privatekey);
      WritePrivateKeyFile(privateDataPath,
          "PRIVATE_KEYV1:" + Convert.ToBase64String(((Ed25519PrivateKeyParameters)_keyPair.Private).GetEncoded()));
      File.WriteAllText(publicDataPath,
          "PUBLIC_KEYV1:" + Convert.ToBase64String(((Ed25519PublicKeyParameters)_keyPair.Public).GetEncoded()));

      PublicKeyText = Convert.ToBase64String(((Ed25519PublicKeyParameters)_keyPair.Public).GetEncoded());
      AppendLog("Key generated. Protect your private key, if lost there is no method to get it back.");
    } catch (Exception ex) {
      AppendLog("Generate key failed: " + ex.Message);
    }
  }

  public void LoadPrivateKey(string path) {
    try {
      var pem = File.ReadAllText(path);
      if (pem.Contains("PRIVATE_KEYV1")) {
        pem = pem.Replace("PRIVATE_KEYV1:", "");
        var keyap = Convert.FromBase64String(pem.Trim());
        _keyPair = SignedFW.GenerateKey(keyap);
      } else {
        PemReader pr = new PemReader(new StringReader(pem));
        var key = (Ed25519PrivateKeyParameters)pr.ReadObject();
        _keyPair = new AsymmetricCipherKeyPair(key.GeneratePublicKey(), key);
      }
      PublicKeyText = Convert.ToBase64String(((Ed25519PublicKeyParameters)_keyPair.Public).GetEncoded());
      AppendLog("Private key loaded.");
    } catch (Exception ex) {
      AppendLog("Load private key failed: " + ex.Message);
    }
  }

  public void SignBootloader(string binPath) {
    if (_keyPair == null) {
      AppendLog("Load or generate a key first.");
      return;
    }
    try {
      BootloaderPath = binPath;
      var ms = SignedFW.CreateSignedBL(_keyPair, binPath);
      var outPath = Path.Combine(Path.GetDirectoryName(binPath)!,
          Path.GetFileNameWithoutExtension(binPath) + "-signed.bin");
      File.WriteAllBytes(outPath, ms);
      AppendLog("Signed bootloader written: " + outPath);
    } catch (Exception ex) {
      AppendLog("Sign bootloader failed: " + ex.Message);
    }
  }

  public void SignFirmware(string apjPath) {
    if (_keyPair == null) {
      AppendLog("Load or generate a key first.");
      return;
    }
    try {
      FirmwarePath = apjPath;
      var output = SignedFW.CreateSignedAPJ(_keyPair, apjPath);
      var outPath = Path.Combine(Path.GetDirectoryName(apjPath)!,
          Path.GetFileNameWithoutExtension(apjPath) + "-signed.apj");
      File.WriteAllBytes(outPath, output);
      AppendLog("Signed firmware written: " + outPath);
    } catch (Exception ex) {
      AppendLog("Sign firmware failed: " + ex.Message);
    }
  }

  internal static (string PemPath, string PrivateDataPath, string PublicDataPath) KeyOutputPaths(
      string pemSavePath) {
    if (string.IsNullOrWhiteSpace(pemSavePath)) {
      throw new ArgumentException("A private-key path is required.", nameof(pemSavePath));
    }
    string pemPath = Path.GetFullPath(pemSavePath);
    string? directory = Path.GetDirectoryName(pemPath);
    string stem = Path.GetFileNameWithoutExtension(pemPath);
    if (directory == null || stem.Length == 0) {
      throw new ArgumentException("The private-key path must contain a file name.",
          nameof(pemSavePath));
    }
    return (
        pemPath,
        Path.Combine(directory, stem + "_private_key.dat"),
        Path.Combine(directory, stem + "_public_key.dat"));
  }

  private static void WritePrivateKeyFile(string path, string contents) {
    string? directory = Path.GetDirectoryName(path);
    if (directory == null) {
      throw new ArgumentException("The private-key path has no parent directory.", nameof(path));
    }
    string temporary = Path.Combine(
        directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    var options = new FileStreamOptions {
      Mode = FileMode.CreateNew,
      Access = FileAccess.Write,
      Share = FileShare.None,
    };
    if (!OperatingSystem.IsWindows()) {
      options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }
    try {
      using (var stream = new FileStream(temporary, options))
      using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
        writer.Write(contents);
      }
      File.Move(temporary, path, overwrite: true);
      if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
      }
    } catch {
      try {
        File.Delete(temporary);
      } catch {
      }
      throw;
    }
  }

  private void AppendLog(string line) {
    Log += $"{DateTime.Now:HH:mm:ss}  {line}\n";
  }
}
