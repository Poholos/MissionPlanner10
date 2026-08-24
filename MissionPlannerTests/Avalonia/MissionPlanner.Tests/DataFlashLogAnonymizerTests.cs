using System.Buffers.Binary;
using System.Text;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public class DataFlashLogAnonymizerTests {
  [Fact]
  public void Preserves_binary_log_and_offsets_latitude_and_longitude_independently() {
    byte[] format = Format(42, 11, "GPS", "LL", "Lat,Lng");
    byte[] message = Message(42, 11);
    BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(3), 100_000_000);
    BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(7), 200_000_000);
    byte[] input = [.. format, .. message];

    byte[] output = DataFlashLogAnonymizer.AnonymizeBytes(
        input, 1.25, -0.5, out int fields, out int patched);

    Assert.Equal(input.Length, output.Length);
    Assert.Equal(format, output[..format.Length]);
    Assert.Equal(2, fields);
    Assert.Equal(2, patched);
    Assert.Equal(112_500_000,
        BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(format.Length + 3)));
    Assert.Equal(195_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(format.Length + 7)));
  }

  [Fact]
  public void Uses_FMTU_units_and_falls_back_for_message_types_without_FMTU() {
    byte[] fmtuFormat = Format(200, 28, "FMTU", "", "");
    byte[] unitFormat = Format(43, 11, "POS", "ii", "X,Y");
    byte[] fallbackFormat = Format(44, 11, "GPS", "LL", "Lat,Lng");
    byte[] fmtu = Message(200, 28);
    fmtu[11] = 43;
    WriteAscii(fmtu, 12, 16, "DU");
    byte[] unitMessage = Message(43, 11);
    BinaryPrimitives.WriteInt32LittleEndian(unitMessage.AsSpan(3), 10_000_000);
    BinaryPrimitives.WriteInt32LittleEndian(unitMessage.AsSpan(7), 20_000_000);
    byte[] fallbackMessage = Message(44, 11);
    BinaryPrimitives.WriteInt32LittleEndian(fallbackMessage.AsSpan(3), 30_000_000);
    BinaryPrimitives.WriteInt32LittleEndian(fallbackMessage.AsSpan(7), 40_000_000);
    byte[] input = [.. fmtuFormat, .. unitFormat, .. fallbackFormat,
                    .. fmtu, .. unitMessage, .. fallbackMessage];

    byte[] output = DataFlashLogAnonymizer.AnonymizeBytes(
        input, 1, 2, out int fields, out int patched);

    Assert.Equal(4, fields);
    Assert.Equal(4, patched);
    int unitOffset = fmtuFormat.Length + unitFormat.Length + fallbackFormat.Length + fmtu.Length;
    Assert.Equal(20_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(unitOffset + 3)));
    Assert.Equal(40_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(unitOffset + 7)));
    int fallbackOffset = unitOffset + unitMessage.Length;
    Assert.Equal(40_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(fallbackOffset + 3)));
    Assert.Equal(60_000_000,
        BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(fallbackOffset + 7)));
  }

  [Fact]
  public void Rejects_logs_without_coordinate_definitions() {
    byte[] input = [.. Format(50, 7, "TEST", "I", "Count"), .. Message(50, 7)];

    Assert.Throws<InvalidDataException>(() => DataFlashLogAnonymizer.AnonymizeBytes(
        input, 1, 1, out _, out _));
  }

  [Fact]
  public void Generated_offsets_match_the_official_nontrivial_range() {
    for (int index = 0; index < 64; index++) {
      double value = DataFlashLogAnonymizer.GenerateRandomOffset();
      Assert.InRange(Math.Abs(value), 0.5, 2.000001);
    }
  }

  private static byte[] Format(byte type, byte length, string name, string format, string columns) {
    var result = new byte[89];
    result[0] = 0xa3;
    result[1] = 0x95;
    result[2] = 128;
    result[3] = type;
    result[4] = length;
    WriteAscii(result, 5, 4, name);
    WriteAscii(result, 9, 16, format);
    WriteAscii(result, 25, 64, columns);
    return result;
  }

  private static byte[] Message(byte type, int length) {
    var result = new byte[length];
    result[0] = 0xa3;
    result[1] = 0x95;
    result[2] = type;
    return result;
  }

  private static void WriteAscii(byte[] destination, int offset, int length, string text) {
    byte[] bytes = Encoding.ASCII.GetBytes(text);
    bytes.AsSpan(0, Math.Min(bytes.Length, length)).CopyTo(destination.AsSpan(offset, length));
  }
}
