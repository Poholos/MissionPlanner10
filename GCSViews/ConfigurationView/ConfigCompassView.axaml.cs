using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Views.GCSViews.ConfigurationView;

public partial class ConfigCompassView : UserControl {
  public ConfigCompassView() {
    AvaloniaXamlLoader.Load(this);

    DetachedFromVisualTree += (_, _) => {
      if (DataContext is ConfigCompassViewModel vm) {
        vm.Deactivate();
      }
    };
  }
}
