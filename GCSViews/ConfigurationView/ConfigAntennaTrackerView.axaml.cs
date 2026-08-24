using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Views.GCSViews.ConfigurationView;

public partial class ConfigAntennaTrackerView : UserControl {
  public ConfigAntennaTrackerView() {
    AvaloniaXamlLoader.Load(this);
    AttachedToVisualTree += (_, _) => {
      if (DataContext is ConfigAntennaTrackerViewModel vm) {
        vm.Activate();
      }
    };
    DetachedFromVisualTree += (_, _) => {
      if (DataContext is ConfigAntennaTrackerViewModel vm) {
        vm.Deactivate();
      }
    };
  }
}
