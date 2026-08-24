using System.Collections.Generic;
using MissionPlanner.Services;
using Xunit;

namespace MissionPlanner.Tests;

public class SikRadioSettingsServiceTests {
  [Fact]
  public void ParseMetadataSupportsClassicRfdAndMultipointReplies() {
    const string reply = "S2:AIR_SPEED(L)[4..1000]=125{4,64,125,250,500,1000,}\r\n"
        + "[7]S15:MAX_WINDOW(L)[20..400]=80\r\nS16:RESERVED(L)[0..1]=0\r\n";

    var parsed = SikRadioSettingsService.ParseMetadata(reply);

    Assert.Equal(new[] { "4", "64", "125", "250", "500", "1000" },
        parsed["AIR_SPEED"].AllowedValues);
    Assert.Contains("80", parsed["MAX_WINDOW"].AllowedValues);
    Assert.DoesNotContain("RESERVED", parsed.Keys);
  }

  [Fact]
  public void ParseMetadataScalesRawSerialBaudOptions() {
    const string reply = "S1:SERIAL_SPEED(L)[1..460]=57{1200,2400,4800,9600,19200,38400,57600,115200,230400,460800,}";

    var parsed = SikRadioSettingsService.ParseMetadata(reply);

    Assert.Equal(new[] { "1", "2", "4", "9", "19", "38", "57", "115", "230", "460" },
        parsed["SERIAL_SPEED"].AllowedValues);
  }

  [Fact]
  public void ProfilesRemainCompatibleWithOfficialNameEqualsValueFormat() {
    const string input = "# RFD profile\nNETID = 42 ; field unit\nAESKEY=ABCDEF\ninvalid\nUNKNOWN = 9\n";

    SikRadioProfile parsed = SikRadioSettingsService.ParseProfile(input);
    string saved = SikRadioSettingsService.SerializeProfile(parsed.Values);

    Assert.Equal("ABCDEF", parsed.Values["AESKEY"]);
    Assert.Equal(1, parsed.IgnoredLines);
    Assert.Equal("AESKEY = ABCDEF\nNETID = 42\nUNKNOWN = 9\n", saved.Replace("\r\n", "\n"));
  }

  [Fact]
  public void LargeRangesStayFreeTextInsteadOfCreatingHugeDropDowns() {
    Assert.Empty(SikRadioSettingsService.BuildRange(0, 65535, 1));
    Assert.True(SikRadioSettingsService.IsValidHexKey("00aBcD"));
    Assert.False(SikRadioSettingsService.IsValidHexKey("not-a-key"));
  }
}
