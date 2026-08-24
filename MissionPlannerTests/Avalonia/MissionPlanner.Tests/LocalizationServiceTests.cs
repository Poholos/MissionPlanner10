using System.Globalization;
using MissionPlanner.Services;

namespace MissionPlanner.Tests;

public sealed class LocalizationServiceTests {
  [Fact]
  public void CultureCodeResolvesToSupportedCulture() {
    CultureInfo culture = LocalizationService.Resolve("ru-RU", CultureInfo.GetCultureInfo("en-US"));

    Assert.Equal("ru-RU", culture.Name);
    Assert.Equal(culture.DisplayName,
        LocalizationService.DisplayForSetting("ru-RU", CultureInfo.GetCultureInfo("en-US")));
  }

  [Fact]
  public void LegacyDisplayNameResolvesToCultureCode() {
    CultureInfo russian = CultureInfo.GetCultureInfo("ru-RU");

    CultureInfo culture = LocalizationService.Resolve(
        russian.DisplayName, CultureInfo.GetCultureInfo("en-US"));

    Assert.Equal("ru-RU", culture.Name);
  }

  [Fact]
  public void InvalidSettingFallsBackToSystemCulture() {
    CultureInfo system = CultureInfo.GetCultureInfo("de-DE");

    Assert.Equal(system, LocalizationService.Resolve("not-a-real-culture", system));
  }

  [Fact]
  public void ParentCultureMatchesSpecificChild() {
    Assert.True(LocalizationService.IsChildOf(
        CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.GetCultureInfo("fr")));
    Assert.False(LocalizationService.IsChildOf(
        CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.GetCultureInfo("de")));
  }
}
