using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace MissionPlanner.Services;

internal static class LocalizationService {
  private static readonly string[] SupportedCultureNames = [
    "en-US", "zh-Hans", "zh-TW", "ru-RU", "fr", "pl", "it-IT", "es-ES", "de-DE",
    "ja-JP", "id-ID", "ko-KR", "ar", "pt", "tr", "ru-KZ", "uk",
  ];

  internal static IReadOnlyList<CultureInfo> SupportedCultures { get; } =
      SupportedCultureNames.Select(CultureInfo.GetCultureInfo).ToArray();

  internal static IReadOnlyList<string> DisplayNames { get; } =
      SupportedCultures.Select(culture => culture.DisplayName).ToArray();

  internal static CultureInfo Resolve(string? setting, CultureInfo systemCulture) {
    if (string.IsNullOrWhiteSpace(setting)
        || string.Equals(setting, "System", StringComparison.OrdinalIgnoreCase)) {
      return systemCulture;
    }

    try {
      CultureInfo byName = CultureInfo.GetCultureInfo(setting.Trim());
      return SupportedCultures.FirstOrDefault(culture => IsChildOf(byName, culture))
             ?? systemCulture;
    } catch (CultureNotFoundException) {
      return SupportedCultures.FirstOrDefault(culture =>
                 string.Equals(culture.DisplayName, setting.Trim(),
                     StringComparison.CurrentCultureIgnoreCase)
                 || string.Equals(culture.EnglishName, setting.Trim(),
                     StringComparison.OrdinalIgnoreCase))
             ?? systemCulture;
    }
  }

  internal static string DisplayForSetting(string? setting, CultureInfo systemCulture) {
    CultureInfo resolved = Resolve(setting, systemCulture);
    return SupportedCultures.FirstOrDefault(culture => IsChildOf(resolved, culture))?.DisplayName
           ?? SupportedCultures[0].DisplayName;
  }

  internal static bool TryCultureForDisplay(string? displayName, out CultureInfo culture) {
    CultureInfo? found = SupportedCultures.FirstOrDefault(item =>
        string.Equals(item.DisplayName, displayName?.Trim(),
            StringComparison.CurrentCultureIgnoreCase));
    culture = found ?? CultureInfo.InvariantCulture;
    return found != null;
  }

  internal static CultureInfo ApplySaved() {
    CultureInfo systemCulture = CultureInfo.CurrentUICulture;
    CultureInfo selected = Resolve(MissionPlanner.Utilities.Settings.Instance["language"],
        systemCulture);
    CultureInfo.DefaultThreadCurrentUICulture = selected;
    Thread.CurrentThread.CurrentUICulture = selected;
    MissionPlanner.Strings.Culture = selected;
    return selected;
  }

  internal static bool IsChildOf(CultureInfo culture, CultureInfo parent) {
    for (CultureInfo current = culture;
         current != CultureInfo.InvariantCulture;
         current = current.Parent) {
      if (current.Equals(parent)) {
        return true;
      }
    }
    return false;
  }
}
