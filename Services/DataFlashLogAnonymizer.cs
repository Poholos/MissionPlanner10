using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MissionPlanner.Services;

internal readonly record struct DataFlashAnonymizeResult(
    int CoordinateFields, int PatchedValues, long InputBytes);

/// <summary>
/// Binary-preserving port of the official AnonymizeBinlog plugin. DataFlash FMT/FMTU records are
/// used to locate coordinate fields; all unrelated bytes and record framing remain unchanged.
/// </summary>
internal static class DataFlashLogAnonymizer {
  private const byte Head1 = 0xa3;
  private const byte Head2 = 0x95;
  private const byte FmtMessageId = 128;
  private const int FmtMessageLength = 89;
  private const char LatitudeUnit = 'D';
  private const char LongitudeUnit = 'U';

  private static readonly HashSet<string> LatitudeNames = new(
      ["lat", "hlat", "dlat", "oalat", "dlt", "olt", "elat",
       "olat", "clat", "trlat", "wplat", "rlat", "tp_lat"],
      StringComparer.OrdinalIgnoreCase);

  private static readonly HashSet<string> LongitudeNames = new(
      ["lng", "lon", "hlon", "hlng", "dlng", "oalng", "dlg", "olg",
       "elng", "olng", "clng", "trlng", "wplng", "rlng", "tp_lng"],
      StringComparer.OrdinalIgnoreCase);

  private static readonly Dictionary<char, int> FormatSizes = new() {
    ['a'] = 64, ['b'] = 1, ['B'] = 1, ['c'] = 2, ['C'] = 2,
    ['d'] = 8, ['e'] = 4, ['E'] = 4, ['f'] = 4, ['h'] = 2,
    ['H'] = 2, ['i'] = 4, ['I'] = 4, ['L'] = 4, ['M'] = 1,
    ['n'] = 4, ['N'] = 16, ['Z'] = 64, ['q'] = 8, ['Q'] = 8,
    ['A'] = 128,
  };

  private sealed record FormatDefinition(string Name, int Length, string Format, string[] Columns);
  private readonly record struct FieldOffset(int ByteOffset, char Format, int Size);
  private readonly record struct CoordinatePatch(int ByteOffset, char Format, int Size, bool Latitude);

  internal static double GenerateRandomOffset() {
    double magnitude = 0.5 + RandomNumberGenerator.GetInt32(0, 1_500_001) / 1_000_000d;
    return RandomNumberGenerator.GetInt32(2) == 0 ? -magnitude : magnitude;
  }

  internal static DataFlashAnonymizeResult AnonymizeFile(
      string inputPath, string outputPath, double latitudeOffset, double longitudeOffset) {
    ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
    if (!double.IsFinite(latitudeOffset) || !double.IsFinite(longitudeOffset)) {
      throw new ArgumentOutOfRangeException(nameof(latitudeOffset),
          "Coordinate offsets must be finite numbers.");
    }

    byte[] input = File.ReadAllBytes(inputPath);
    byte[] output = AnonymizeBytes(
        input, latitudeOffset, longitudeOffset, out int fields, out int patched);
    string destination = Path.GetFullPath(outputPath);
    string directory = Path.GetDirectoryName(destination)
        ?? throw new InvalidOperationException("The output path has no parent directory.");
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(directory,
        $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    try {
      File.WriteAllBytes(temporary, output);
      File.Move(temporary, destination, overwrite: true);
    } finally {
      try {
        File.Delete(temporary);
      } catch {
      }
    }
    return new DataFlashAnonymizeResult(fields, patched, input.LongLength);
  }

  internal static byte[] AnonymizeBytes(
      byte[] data,
      double latitudeOffset,
      double longitudeOffset,
      out int coordinateFields,
      out int patchedValues) {
    ArgumentNullException.ThrowIfNull(data);
    if (!double.IsFinite(latitudeOffset) || !double.IsFinite(longitudeOffset)) {
      throw new ArgumentOutOfRangeException(nameof(latitudeOffset),
          "Coordinate offsets must be finite numbers.");
    }

    Dictionary<int, FormatDefinition> formats = ParseFormats(data);
    Dictionary<int, string> units = ParseFmtu(data, formats);
    Dictionary<int, List<CoordinatePatch>> patches = IdentifyCoordinateFields(formats, units);
    coordinateFields = patches.Values.Sum(items => items.Count);
    if (coordinateFields == 0) {
      throw new InvalidDataException("No coordinate fields were found in the DataFlash log.");
    }

    byte[] output = (byte[])data.Clone();
    patchedValues = 0;
    int position = 0;
    while (position + 2 < data.Length) {
      if (data[position] != Head1 || data[position + 1] != Head2) {
        position++;
        continue;
      }
      byte messageId = data[position + 2];
      if (messageId == FmtMessageId) {
        position = Advance(position, FmtMessageLength, data.Length);
        continue;
      }
      if (!formats.TryGetValue(messageId, out FormatDefinition? definition)
          || !ValidRecordLength(definition.Length)
          || position + definition.Length > data.Length) {
        position++;
        continue;
      }

      if (patches.TryGetValue(messageId, out List<CoordinatePatch>? messagePatches)) {
        foreach (CoordinatePatch patch in messagePatches) {
          int offset = position + patch.ByteOffset;
          if (offset < position + 3 || offset + patch.Size > position + definition.Length
              || !TryReadValue(data, offset, patch.Format, out double oldValue)
              || oldValue == 0) {
            continue;
          }
          double degrees = patch.Latitude ? latitudeOffset : longitudeOffset;
          double value = OffsetValue(oldValue, patch.Format, degrees);
          if (TryWriteValue(output, offset, patch.Format, value)) {
            patchedValues++;
          }
        }
      }
      position += definition.Length;
    }
    return output;
  }

  private static Dictionary<int, FormatDefinition> ParseFormats(byte[] data) {
    var definitions = new Dictionary<int, FormatDefinition>();
    int position = 0;
    while (position + 2 < data.Length) {
      if (data[position] != Head1 || data[position + 1] != Head2) {
        position++;
        continue;
      }
      byte messageId = data[position + 2];
      if (messageId == FmtMessageId) {
        if (position + FmtMessageLength <= data.Length) {
          byte typeId = data[position + 3];
          int length = data[position + 4];
          string name = ReadAscii(data, position + 5, 4);
          string format = ReadAscii(data, position + 9, 16);
          string columns = ReadAscii(data, position + 25, 64);
          if (ValidRecordLength(length)) {
            definitions[typeId] = new FormatDefinition(
                name, length, format, columns.Split(','));
          }
        }
        position = Advance(position, FmtMessageLength, data.Length);
      } else if (definitions.TryGetValue(messageId, out FormatDefinition? definition)
          && ValidRecordLength(definition.Length)) {
        position = Advance(position, definition.Length, data.Length);
      } else {
        position++;
      }
    }
    return definitions;
  }

  private static Dictionary<int, string> ParseFmtu(
      byte[] data, IReadOnlyDictionary<int, FormatDefinition> definitions) {
    KeyValuePair<int, FormatDefinition> pair =
        definitions.FirstOrDefault(item => item.Value.Name == "FMTU");
    if (pair.Value == null || !ValidRecordLength(pair.Value.Length)) {
      return [];
    }
    int fmtuType = pair.Key;
    FormatDefinition fmtu = pair.Value;

    var map = new Dictionary<int, string>();
    int position = 0;
    while (position + 2 < data.Length) {
      if (data[position] != Head1 || data[position + 1] != Head2) {
        position++;
        continue;
      }
      byte messageId = data[position + 2];
      if (messageId == fmtuType && position + fmtu.Length <= data.Length
          && fmtu.Length >= 28) {
        byte describedType = data[position + 11];
        map[describedType] = ReadAscii(data, position + 12, 16);
        position += fmtu.Length;
      } else if (definitions.TryGetValue(messageId, out FormatDefinition? definition)
          && ValidRecordLength(definition.Length)) {
        position = Advance(position, definition.Length, data.Length);
      } else if (messageId == FmtMessageId) {
        position = Advance(position, FmtMessageLength, data.Length);
      } else {
        position++;
      }
    }
    return map;
  }

  private static Dictionary<int, List<CoordinatePatch>> IdentifyCoordinateFields(
      IReadOnlyDictionary<int, FormatDefinition> definitions,
      IReadOnlyDictionary<int, string> unitIds) {
    var result = new Dictionary<int, List<CoordinatePatch>>();
    foreach ((int typeId, FormatDefinition definition) in definitions) {
      if (definition.Name is "FMT" or "FMTU" or "MULT" or "UNIT") {
        continue;
      }
      List<FieldOffset> offsets = ComputeFieldOffsets(definition.Format);
      int count = Math.Min(offsets.Count, definition.Columns.Length);
      var matches = new List<CoordinatePatch>();

      // FMTU is authoritative for fields it describes. Some real logs omit FMTU for only a
      // subset of message types, so use the official name fallback for those types instead of
      // silently leaving their coordinates untouched.
      if (unitIds.TryGetValue(typeId, out string? units)) {
        for (int index = 0; index < units.Length && index < count; index++) {
          if (units[index] is LatitudeUnit or LongitudeUnit) {
            FieldOffset field = offsets[index];
            matches.Add(new CoordinatePatch(
                field.ByteOffset, field.Format, field.Size, units[index] == LatitudeUnit));
          }
        }
      } else {
        for (int index = 0; index < count; index++) {
          string name = definition.Columns[index];
          bool? latitude = CoordinateKind(name, offsets[index].Format);
          if (latitude.HasValue) {
            FieldOffset field = offsets[index];
            matches.Add(new CoordinatePatch(
                field.ByteOffset, field.Format, field.Size, latitude.Value));
          }
        }
      }
      if (matches.Count > 0) {
        result[typeId] = matches;
      }
    }
    return result;
  }

  private static bool? CoordinateKind(string name, char format) {
    if (LatitudeNames.Contains(name)) {
      return true;
    }
    if (LongitudeNames.Contains(name)) {
      return false;
    }
    if (format != 'L') {
      return null;
    }
    string lower = name.ToLowerInvariant();
    if (lower.Contains("lat", StringComparison.Ordinal) || lower.Contains("lt", StringComparison.Ordinal)) {
      return true;
    }
    if (lower.Contains("lng", StringComparison.Ordinal) || lower.Contains("lon", StringComparison.Ordinal)
        || lower.Contains("lg", StringComparison.Ordinal)) {
      return false;
    }
    return null;
  }

  private static List<FieldOffset> ComputeFieldOffsets(string format) {
    int offset = 3;
    var result = new List<FieldOffset>();
    foreach (char item in format) {
      if (!FormatSizes.TryGetValue(item, out int size)) {
        break;
      }
      result.Add(new FieldOffset(offset, item, size));
      offset += size;
    }
    return result;
  }

  private static bool TryReadValue(byte[] data, int offset, char format, out double value) {
    ReadOnlySpan<byte> bytes = data.AsSpan(offset);
    value = format switch {
      'b' => (sbyte)bytes[0],
      'B' or 'M' => bytes[0],
      'c' or 'h' => BinaryPrimitives.ReadInt16LittleEndian(bytes),
      'C' or 'H' => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
      'e' or 'i' or 'L' => BinaryPrimitives.ReadInt32LittleEndian(bytes),
      'E' or 'I' => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
      'f' => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
      'd' => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
      'q' => BinaryPrimitives.ReadInt64LittleEndian(bytes),
      'Q' => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
      _ => double.NaN,
    };
    return double.IsFinite(value);
  }

  private static bool TryWriteValue(byte[] data, int offset, char format, double value) {
    Span<byte> bytes = data.AsSpan(offset);
    switch (format) {
      case 'L':
      case 'i':
        BinaryPrimitives.WriteInt32LittleEndian(bytes, unchecked((int)value));
        return true;
      case 'I':
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)value));
        return true;
      case 'f':
        BinaryPrimitives.WriteInt32LittleEndian(bytes,
            BitConverter.SingleToInt32Bits(unchecked((float)value)));
        return true;
      case 'd':
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        return true;
      default:
        return false;
    }
  }

  private static double OffsetValue(double oldValue, char format, double degrees) => format switch {
    'L' or 'i' => unchecked((int)(oldValue + degrees * 1e7)),
    'I' => unchecked((uint)(unchecked((int)(oldValue + degrees * 1e7)))),
    'f' => Math.Abs(oldValue) > 1000 ? oldValue + degrees * 1e7 : oldValue + degrees,
    'd' => oldValue + degrees,
    _ => oldValue,
  };

  private static string ReadAscii(byte[] data, int offset, int length) {
    if (offset < 0 || length <= 0 || offset >= data.Length) {
      return "";
    }
    int available = Math.Min(length, data.Length - offset);
    int terminator = Array.IndexOf(data, (byte)0, offset, available);
    int actual = terminator >= 0 ? terminator - offset : available;
    return Encoding.ASCII.GetString(data, offset, actual);
  }

  private static bool ValidRecordLength(int length) => length >= 3;

  private static int Advance(int position, int amount, int length) =>
      amount > 0 && position <= length - amount ? position + amount : position + 1;
}
