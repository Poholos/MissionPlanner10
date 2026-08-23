using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Views.GCSViews.ConfigurationView;

public partial class ConfigCompassLegacyView : UserControl {
  public ConfigCompassLegacyView() {
    AvaloniaXamlLoader.Load(this);

    DetachedFromVisualTree += (_, _) => {
      if (DataContext is ConfigCompassLegacyViewModel vm) {
        vm.Deactivate();
      }
    };
  }
}
