using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MissionPlanner.Comms;
using MissionPlanner.Radio;

namespace MissionPlanner.Services;

/// <summary>
/// Model validation and the X-series XModem bootloader protocol used by RFD900x/ux radios.
/// The older SiK bootloader remains implemented by <see cref="Uploader"/>.
/// </summary>
public static class SikRadioFirmwareService {
  private const byte Soh = 0x01;
  private const byte Eot = 0x04;
  private const byte Ack = 0x06;

  public static bool TryParseBoard(string response, out Uploader.Board board) {
    string value = StripMultipointPrefix(response).Trim();
    NumberStyles style = NumberStyles.Integer;
    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
      value = value[2..];
      style = NumberStyles.HexNumber;
    }
    if (byte.TryParse(value, style, CultureInfo.InvariantCulture, out byte code)
        && Enum.IsDefined(typeof(Uploader.Board), code)) {
      board = (Uploader.Board)code;
      return true;
    }
    board = Uploader.Board.FAILED;
    return false;
  }

  public static bool UsesXModem(Uploader.Board board) => board is
      Uploader.Board.DEVICE_ID_RFD900X or Uploader.Board.DEVICE_ID_RFD900UX
      or Uploader.Board.DEVICE_ID_RFD900X2 or Uploader.Board.DEVICE_ID_RFD900UX2;

  public static bool UsesHighSpeedXModem(Uploader.Board board) => board is
      Uploader.Board.DEVICE_ID_RFD900X2 or Uploader.Board.DEVICE_ID_RFD900UX2;

  public static int? CountryRegister(Uploader.Board board) => board switch {
    Uploader.Board.DEVICE_ID_RFD900X or Uploader.Board.DEVICE_ID_RFD900UX => 32,
    Uploader.Board.DEVICE_ID_RFD900X2 or Uploader.Board.DEVICE_ID_RFD900UX2 => 51,
    _ => null,
  };

  public static bool IsCountryLocked(string response) =>
      byte.TryParse(StripMultipointPrefix(response).Trim(), NumberStyles.Integer,
          CultureInfo.InvariantCulture, out byte code) && code is not 0 and not 255;

  public static string? StableFirmwareUrl(Uploader.Board board, bool beta) {
    string channel = beta ? "beta" : "stable";
    return board switch {
      Uploader.Board.DEVICE_ID_HM_TRP =>
          $"https://firmware.ardupilot.org/SiK/{channel}/radio~hm_trp.ihx",
      Uploader.Board.DEVICE_ID_HB1060 =>
          $"https://firmware.ardupilot.org/SiK/{channel}/radio~hb1060.ihx",
      Uploader.Board.DEVICE_ID_RFD900 =>
          $"https://firmware.ardupilot.org/SiK/{channel}/radio~rfd900.ihx",
      Uploader.Board.DEVICE_ID_RFD900A =>
          $"https://firmware.ardupilot.org/SiK/{channel}/radio~rfd900a.ihx",
      Uploader.Board.DEVICE_ID_RFD900U =>
          $"https://firmware.ardupilot.org/SiK/{channel}/radio~rfd900u.ihx",
      Uploader.Board.DEVICE_ID_RFD900P =>
          $"https://firmware.ardupilot.org/SiK/{channel}/radio~rfd900p.ihx",
      Uploader.Board.DEVICE_ID_RFD900X when !beta =>
          "https://files.rfdesign.com.au/Files/firmware/RFDSiK%20V3.57%20rfd900x.bin",
      Uploader.Board.DEVICE_ID_RFD900UX when !beta =>
          "https://files.rfdesign.com.au/Files/firmware/RFDSiK%20V3.57%20rfd900ux.bin",
      Uploader.Board.DEVICE_ID_RFD900X2 when !beta =>
          "https://files.rfdesign.com.au/Files/firmware/RFDSiK%20V3.57%20rfd900x2.gbl",
      Uploader.Board.DEVICE_ID_RFD900UX2 when !beta =>
          "https://files.rfdesign.com.au/Files/firmware/RFDSiK%20V3.57%20rfd900ux2.gbl",
      _ => null,
    };
  }

  public static bool ValidateImage(Uploader.Board board, string path, bool countryLocked,
      out string error) {
    string extension = Path.GetExtension(path).ToLowerInvariant();
    string fileName = Path.GetFileName(path);
    string[] tokens;
    switch (board) {
      case Uploader.Board.DEVICE_ID_HM_TRP:
        tokens = ["HM-TRP"];
        break;
      case Uploader.Board.DEVICE_ID_HB1060:
        tokens = ["HB1060"];
        break;
      case Uploader.Board.DEVICE_ID_RFD900:
        tokens = ["RFD900"];
        break;
      case Uploader.Board.DEVICE_ID_RFD900A:
        tokens = ["RFD900A"];
        break;
      case Uploader.Board.DEVICE_ID_RFD900P:
        tokens = ["RFD900P"];
        break;
      case Uploader.Board.DEVICE_ID_RFD900U:
        tokens = ["RFD900U"];
        break;
      case Uploader.Board.DEVICE_ID_RFD900X:
        return ValidateBinary(path, extension, ["RFD900x", "RFD900X"], countryLocked, out error);
      case Uploader.Board.DEVICE_ID_RFD900UX:
        return ValidateBinary(path, extension, ["RFD900ux", "RFD900UX"], countryLocked, out error);
      case Uploader.Board.DEVICE_ID_RFD900X2:
        return ValidateGbl(fileName, extension, "rfd900x2", out error);
      case Uploader.Board.DEVICE_ID_RFD900UX2:
        return ValidateGbl(fileName, extension, "rfd900ux2", out error);
      default:
        error = $"Board {board} is not supported by the SiK/RFD firmware uploader.";
        return false;
    }

    if (extension is not ".hex" and not ".ihx") {
      error = $"Board {board} requires an Intel HEX (.hex/.ihx) image.";
      return false;
    }
    try {
      var image = new IHex();
      image.load(path);
      if (!ContainsAny(image.Values.SelectMany(bytes => bytes), tokens)) {
        error = $"The image does not identify itself as {string.Join(" or ", tokens)}.";
        return false;
      }
      error = "";
      return true;
    } catch (Exception ex) {
      error = "Invalid Intel HEX image: " + ex.Message;
      return false;
    }
  }

  public static ushort CalculateCrc(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (byte value in data) {
      crc = (ushort)((crc >> 8) | (crc << 8));
      crc ^= value;
      crc ^= (ushort)((crc & 0xff) >> 4);
      crc ^= (ushort)(crc << 12);
      crc ^= (ushort)((crc & 0xff) << 5);
    }
    return crc;
  }

  public static bool UploadXModem(string path, ICommsSerial port, bool highSpeed,
      Action<string, double>? progress = null) {
    port.BaudRate = 57600;
    port.ReadTimeout = 2000;
    port.Write("U");
    Thread.Sleep(200);
    port.Write("\r\n");
    Thread.Sleep(100);
    port.DiscardInBuffer();
    port.Write("CHIPID\r\n");
    if (!WaitForToken(port, "RFD", 1500)) {
      progress?.Invoke("RFD X-series bootloader did not answer CHIPID.", double.NaN);
      return false;
    }

    if (highSpeed) {
      port.DiscardInBuffer();
      port.Write("BAUDHI\r\n");
      if (!WaitForToken(port, "OK", 1000)) {
        progress?.Invoke("Bootloader rejected high-speed upload mode.", double.NaN);
        return false;
      }
      port.BaudRate = 1200000;
    }

    port.DiscardInBuffer();
    port.Write("\rUPLOAD\r");
    if (!WaitForToken(port, "Ready", 5000) || !WaitForToken(port, "C", 5000)) {
      progress?.Invoke("Bootloader did not start XModem receive mode.", double.NaN);
      return false;
    }

    byte[] image = File.ReadAllBytes(path);
    int blocks = (image.Length + 127) / 128;
    for (int index = 0; index < blocks; index++) {
      int count = Math.Min(128, image.Length - index * 128);
      byte[] packet = CreatePacket(image.AsSpan(index * 128, count), index + 1);
      bool accepted = false;
      for (int retry = 0; retry < 10 && !accepted; retry++) {
        port.DiscardInBuffer();
        port.Write(packet, 0, packet.Length);
        accepted = ReadResponse(port) == Ack;
      }
      if (!accepted) {
        progress?.Invoke($"Block {index + 1}/{blocks} was rejected.", (double)index / blocks);
        return false;
      }
      progress?.Invoke($"Uploading block {index + 1}/{blocks}", (double)(index + 1) / blocks);
    }

    bool finished = false;
    for (int retry = 0; retry < 10 && !finished; retry++) {
      port.Write([Eot], 0, 1);
      finished = ReadResponse(port) == Ack;
    }
    if (!finished) {
      return false;
    }

    Thread.Sleep(100);
    port.Write("\r\nBOOTNEW\r\n");
    progress?.Invoke("Firmware uploaded; modem reboot requested.", 1);
    return true;
  }

  internal static byte[] CreatePacket(ReadOnlySpan<byte> data, int blockNumber) {
    var packet = new byte[133];
    packet.AsSpan(3, 128).Fill(0x26);
    packet[0] = Soh;
    packet[1] = (byte)blockNumber;
    packet[2] = (byte)(255 - packet[1]);
    data.CopyTo(packet.AsSpan(3, data.Length));
    ushort crc = CalculateCrc(packet.AsSpan(3, 128));
    packet[131] = (byte)(crc >> 8);
    packet[132] = (byte)crc;
    return packet;
  }

  private static bool ValidateBinary(string path, string extension, string[] tokens,
      bool countryLocked, out string error) {
    if (extension != ".bin") {
      error = "This RFD X-series modem requires a .bin image.";
      return false;
    }
    byte[] bytes;
    try {
      bytes = File.ReadAllBytes(path);
    } catch (Exception ex) {
      error = "Unable to read firmware image: " + ex.Message;
      return false;
    }
    if (!ContainsAny(bytes, tokens)) {
      error = $"The image does not identify itself as {string.Join(" or ", tokens)}.";
      return false;
    }
    if (countryLocked && !ContainsAny(bytes, ["HastaLaVistaBaby"])) {
      error = "The modem is country-locked and the selected image is not RFDesign-certified.";
      return false;
    }
    error = "";
    return true;
  }

  private static bool ValidateGbl(string fileName, string extension, string model,
      out string error) {
    if (extension != ".gbl" || !fileName.Contains(model, StringComparison.OrdinalIgnoreCase)) {
      error = $"This modem requires a .gbl image whose file name identifies {model}.";
      return false;
    }
    error = "";
    return true;
  }

  private static bool ContainsAny(IEnumerable<byte> bytes, IEnumerable<string> tokens) {
    byte[] data = bytes as byte[] ?? bytes.ToArray();
    string ascii = Encoding.ASCII.GetString(data);
    return tokens.Any(token => ascii.Contains(token, StringComparison.Ordinal));
  }

  private static int ReadResponse(ICommsSerial port) {
    try {
      int value;
      do {
        value = port.ReadByte();
      } while (value == 'C');
      return value;
    } catch {
      return -1;
    }
  }

  private static bool WaitForToken(ICommsSerial port, string token, int timeoutMs) {
    int oldTimeout = port.ReadTimeout;
    port.ReadTimeout = Math.Min(timeoutMs, 250);
    var received = new StringBuilder();
    long deadline = Environment.TickCount64 + timeoutMs;
    try {
      while (Environment.TickCount64 < deadline) {
        try {
          int value = port.ReadByte();
          if (value >= 0) {
            received.Append((char)value);
            if (received.ToString().Contains(token, StringComparison.OrdinalIgnoreCase)) {
              return true;
            }
            if (received.Length > 512) {
              received.Remove(0, received.Length - 256);
            }
          }
        } catch (TimeoutException) {
        }
      }
      return false;
    } finally {
      port.ReadTimeout = oldTimeout;
    }
  }

  private static string StripMultipointPrefix(string value) {
    value = value.Trim();
    int end = value.StartsWith('[') ? value.IndexOf(']') : -1;
    return end >= 0 ? value[(end + 1)..].Trim() : value;
  }
}
