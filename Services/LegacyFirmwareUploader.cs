using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MissionPlanner.Arduino;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services;

internal enum LegacyFirmwareTarget {
  Px4Bootloader,
  Stm32Dfu,
  Stm32DfuBinary,
  Apm1280,
  Apm2560,
  Apm2560V2,
}

internal static class LegacyFirmwareUploader {
  internal const int MaximumHexImageSize = 1024 * 1024;
  private const int Apm1280MaximumImageSize = 126976;
  private const int VerifyBlockSize = 0x100;
  private const int Stm32FlashBase = 0x08000000;
  private static readonly object DfuSync = new();

  internal static LegacyFirmwareTarget? InferManifestTarget(string? format, string? platform) {
    if (string.Equals(format, "apj", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "px4", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "vrx", StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Px4Bootloader;
    }

    if (string.Equals(format, "dfu", StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Stm32Dfu;
    }

    if (!string.Equals(format, "hex", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(platform)) {
      return null;
    }

    if (platform.Equals("apm1-1280", StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Apm1280;
    }
    if (platform.StartsWith("apm1", StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Apm2560;
    }
    if (platform.StartsWith("apm2", StringComparison.OrdinalIgnoreCase)) {
      return LegacyFirmwareTarget.Apm2560V2;
    }

    return null;
  }

  internal static bool RequiresSerialPort(LegacyFirmwareTarget target) =>
      target is LegacyFirmwareTarget.Apm1280 or LegacyFirmwareTarget.Apm2560 or
          LegacyFirmwareTarget.Apm2560V2;

  internal static string DescribeTarget(LegacyFirmwareTarget target) => target switch {
    LegacyFirmwareTarget.Px4Bootloader => "PX4/ChibiOS/VRBrain bootloader",
    LegacyFirmwareTarget.Stm32Dfu => "STM32 DFU",
    LegacyFirmwareTarget.Stm32DfuBinary => "STM32 DFU binary at 0x08000000",
    LegacyFirmwareTarget.Apm1280 => "APM1 ATmega1280 (STK500)",
    LegacyFirmwareTarget.Apm2560 => "APM1 ATmega2560 (STKv2)",
    LegacyFirmwareTarget.Apm2560V2 => "APM2 ATmega2560 (STKv2)",
    _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
  };

  internal static void ValidateFileTarget(string path, LegacyFirmwareTarget target) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var extension = Path.GetExtension(path);
    var valid = target switch {
      LegacyFirmwareTarget.Px4Bootloader =>
          extension.Equals(".apj", StringComparison.OrdinalIgnoreCase) ||
          extension.Equals(".px4", StringComparison.OrdinalIgnoreCase) ||
          extension.Equals(".vrx", StringComparison.OrdinalIgnoreCase),
      LegacyFirmwareTarget.Stm32Dfu =>
          extension.Equals(".dfu", StringComparison.OrdinalIgnoreCase) ||
          extension.Equals(".hex", StringComparison.OrdinalIgnoreCase),
      LegacyFirmwareTarget.Stm32DfuBinary =>
          extension.Equals(".bin", StringComparison.OrdinalIgnoreCase),
      LegacyFirmwareTarget.Apm1280 or LegacyFirmwareTarget.Apm2560 or
          LegacyFirmwareTarget.Apm2560V2 =>
          extension.Equals(".hex", StringComparison.OrdinalIgnoreCase),
      _ => false,
    };

    if (!valid) {
      throw new InvalidDataException(
          $"{Path.GetFileName(path)} is not a valid file type for {DescribeTarget(target)}.");
    }
  }

  internal static byte[] ParseIntelHex(TextReader reader, int maximumImageSize = MaximumHexImageSize) {
    ArgumentNullException.ThrowIfNull(reader);
    if (maximumImageSize <= 0) {
      throw new ArgumentOutOfRangeException(nameof(maximumImageSize));
    }

    var data = new Dictionary<int, byte>();
    var baseAddress = 0L;
    var eofSeen = false;
    var lineNumber = 0;
    string? line;
    while ((line = reader.ReadLine()) != null) {
      lineNumber++;
      line = line.Trim();
      if (line.Length == 0) {
        continue;
      }
      if (eofSeen) {
        throw InvalidHex(lineNumber, "data appears after the end-of-file record");
      }
      if (!line.StartsWith(':')) {
        throw InvalidHex(lineNumber, "record does not start with ':'");
      }

      var record = ParseRecord(line.AsSpan(1), lineNumber);
      var byteCount = record[0];
      var address = (record[1] << 8) | record[2];
      var type = record[3];
      var payload = record.AsSpan(4, byteCount);

      switch (type) {
        case 0:
          var absoluteAddress = baseAddress + address;
          if (absoluteAddress < 0 || absoluteAddress + byteCount > maximumImageSize) {
            throw InvalidHex(lineNumber,
                $"record exceeds the {maximumImageSize}-byte image limit");
          }
          for (var index = 0; index < byteCount; index++) {
            var targetAddress = checked((int)absoluteAddress + index);
            if (data.TryGetValue(targetAddress, out var existing) && existing != payload[index]) {
              throw InvalidHex(lineNumber,
                  $"record conflicts with data already written at 0x{targetAddress:X}");
            }
            data[targetAddress] = payload[index];
          }
          break;
        case 1:
          RequireRecordShape(lineNumber, address, payload, 0, "end-of-file");
          eofSeen = true;
          break;
        case 2:
          RequireRecordShape(lineNumber, address, payload, 2, "extended-segment-address");
          baseAddress = ((payload[0] << 8) | payload[1]) << 4;
          break;
        case 3:
          RequireRecordShape(lineNumber, address, payload, 4, "start-segment-address");
          break;
        case 4:
          RequireRecordShape(lineNumber, address, payload, 2, "extended-linear-address");
          baseAddress = (long)((payload[0] << 8) | payload[1]) << 16;
          break;
        case 5:
          RequireRecordShape(lineNumber, address, payload, 4, "start-linear-address");
          break;
        default:
          throw InvalidHex(lineNumber, $"unsupported record type {type}");
      }
    }

    if (!eofSeen) {
      throw new InvalidDataException("Intel HEX image does not contain an end-of-file record.");
    }
    if (data.Count == 0) {
      throw new InvalidDataException("Intel HEX image contains no firmware data.");
    }

    var image = Enumerable.Repeat((byte)0xff, data.Keys.Max() + 1).ToArray();
    foreach (var pair in data) {
      image[pair.Key] = pair.Value;
    }
    return image;
  }

  internal static void UploadAvr(
      string path,
      string portName,
      LegacyFirmwareTarget target,
      Action<int, string>? progress = null) {
    ValidateFileTarget(path, target);
    if (!RequiresSerialPort(target)) {
      throw new ArgumentException("The selected target is not an AVR board.", nameof(target));
    }
    ArgumentException.ThrowIfNullOrWhiteSpace(portName);

    byte[] image;
    using (var reader = File.OpenText(path)) {
      image = ParseIntelHex(reader);
    }
    if (target == LegacyFirmwareTarget.Apm1280 && image.Length > Apm1280MaximumImageSize) {
      throw new InvalidDataException(
          $"The image is {image.Length} bytes; APM1 ATmega1280 accepts at most " +
          $"{Apm1280MaximumImageSize} bytes.");
    }

    using IArduinoComms port = target == LegacyFirmwareTarget.Apm1280
      ? new ArduinoSTK { BaudRate = 57600 }
      : new ArduinoSTKv2 { BaudRate = 115200 };
    port.PortName = portName;
    port.DtrEnable = true;
    port.ReadTimeout = 2000;
    port.WriteTimeout = 2000;
    MissionPlanner.Arduino.ProgressEventHandler? handler = progress == null
      ? null
      : new MissionPlanner.Arduino.ProgressEventHandler(progress);
    if (handler != null) {
      port.Progress += handler;
    }

    try {
      progress?.Invoke(0, $"Opening {portName}…");
      port.Open();
      if (!port.connectAP()) {
        throw new IOException($"No compatible {DescribeTarget(target)} bootloader answered on {portName}.");
      }

      progress?.Invoke(0, $"Uploading {image.Length} bytes…");
      if (!port.uploadflash(image, 0, image.Length, 0)) {
        throw new IOException("Firmware upload lost synchronization with the bootloader.");
      }

      progress?.Invoke(0, "Verifying firmware…");
      for (var start = 0; start < image.Length; start += VerifyBlockSize) {
        if (!port.setaddress(start)) {
          throw new IOException($"Bootloader rejected verification address 0x{start:X}.");
        }
        var actual = port.downloadflash(VerifyBlockSize);
        var length = Math.Min(VerifyBlockSize, image.Length - start);
        for (var offset = 0; offset < length; offset++) {
          if (image[start + offset] != actual[offset]) {
            throw new IOException(
                $"Firmware verification failed at 0x{start + offset:X}: expected " +
                $"{image[start + offset]:X2}, read {actual[offset]:X2}.");
          }
        }
        progress?.Invoke((int)((start + length) * 100L / image.Length), "Verifying firmware…");
      }
      progress?.Invoke(100, "Firmware upload and verification complete.");
    } finally {
      if (handler != null) {
        port.Progress -= handler;
      }
      if (port.IsOpen) {
        port.Close();
      }
    }
  }

  internal static void UploadDfu(
      string path,
      LegacyFirmwareTarget target,
      Action<int, string>? progress = null) {
    ValidateFileTarget(path, target);
    if (target is not (LegacyFirmwareTarget.Stm32Dfu or LegacyFirmwareTarget.Stm32DfuBinary)) {
      throw new ArgumentException("The selected target is not an STM32 DFU target.", nameof(target));
    }

    lock (DfuSync) {
      var previousProgress = DFU.Progress;
      string? failure = null;
      try {
        DFU.Progress = (percent, status) => {
          if (percent < 0) {
            failure = status;
          }
          progress?.Invoke(percent, status);
        };
        DFU.Flash(path, target == LegacyFirmwareTarget.Stm32DfuBinary ? Stm32FlashBase : 0);
        if (failure != null) {
          throw new IOException(failure);
        }
      } finally {
        DFU.Progress = previousProgress;
      }
    }
  }

  private static byte[] ParseRecord(ReadOnlySpan<char> encoded, int lineNumber) {
    if (encoded.Length < 10 || encoded.Length % 2 != 0) {
      throw InvalidHex(lineNumber, "record has an invalid length");
    }

    var record = new byte[encoded.Length / 2];
    try {
      for (var index = 0; index < record.Length; index++) {
        record[index] = Convert.ToByte(encoded.Slice(index * 2, 2).ToString(), 16);
      }
    } catch (FormatException ex) {
      throw InvalidHex(lineNumber, "record contains a non-hexadecimal character", ex);
    }

    if (record.Length != record[0] + 5) {
      throw InvalidHex(lineNumber,
          $"byte count declares {record[0]} data bytes but the record length differs");
    }
    if (record.Aggregate(0, (sum, value) => sum + value) % 256 != 0) {
      throw InvalidHex(lineNumber, "checksum mismatch");
    }
    return record;
  }

  private static void RequireRecordShape(
      int lineNumber,
      int address,
      ReadOnlySpan<byte> payload,
      int expectedLength,
      string name) {
    if (address != 0 || payload.Length != expectedLength) {
      throw InvalidHex(lineNumber, $"invalid {name} record");
    }
  }

  private static InvalidDataException InvalidHex(
      int lineNumber, string message, Exception? inner = null) =>
      new($"Invalid Intel HEX at line {lineNumber}: {message}.", inner);
}
