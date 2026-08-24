using System.IO;
using MissionPlanner.Radio;
using MissionPlanner.Services;
using Xunit;

namespace MissionPlanner.Tests;

public class SikRadioFirmwareServiceTests {
  [Theory]
  [InlineData("131", Uploader.Board.DEVICE_ID_RFD900X)]
  [InlineData("0x84", Uploader.Board.DEVICE_ID_RFD900X2)]
  [InlineData("[7] 136", Uploader.Board.DEVICE_ID_RFD900UX)]
  public void ParsesDecimalHexAndMultipointBoardCodes(string reply, Uploader.Board expected) {
    Assert.True(SikRadioFirmwareService.TryParseBoard(reply, out Uploader.Board actual));
    Assert.Equal(expected, actual);
  }

  [Fact]
  public void XModemPacketUsesOfficialPaddingAndCrc() {
    byte[] packet = SikRadioFirmwareService.CreatePacket([1, 2, 3], 1);

    Assert.Equal(133, packet.Length);
    Assert.Equal(1, packet[0]);
    Assert.Equal(1, packet[1]);
    Assert.Equal(254, packet[2]);
    Assert.Equal(0x26, packet[130]);
    ushort crc = SikRadioFirmwareService.CalculateCrc(packet.AsSpan(3, 128));
    Assert.Equal((byte)(crc >> 8), packet[131]);
    Assert.Equal((byte)crc, packet[132]);
  }

  [Fact]
  public void LockedXSeriesRejectsUncertifiedImage() {
    string path = Path.Combine(Path.GetTempPath(), "RFDSiK-test-rfd900x.bin");
    try {
      File.WriteAllText(path, "RFD900x but no certification marker");
      Assert.False(SikRadioFirmwareService.ValidateImage(
          Uploader.Board.DEVICE_ID_RFD900X, path, countryLocked: true, out string error));
      Assert.Contains("country-locked", error);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void X2RequiresModelSpecificGblFileName() {
    Assert.False(SikRadioFirmwareService.ValidateImage(
        Uploader.Board.DEVICE_ID_RFD900UX2, "/tmp/RFDSiK-rfd900x2.gbl", false, out _));
  }
}
