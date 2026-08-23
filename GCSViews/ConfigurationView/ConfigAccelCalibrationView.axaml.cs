using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Views.GCSViews.ConfigurationView;

public partial class ConfigAccelCalibrationView : UserControl {
  public ConfigAccelCalibrationView() {
    AvaloniaXamlLoader.Load(this);
    DetachedFromVisualTree += (_, _) => {
      if (DataContext is ConfigAccelCalibrationViewModel vm) {
        vm.Deactivate();
      }
    };
  }
}
