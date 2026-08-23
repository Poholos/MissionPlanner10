using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MissionPlanner.Views.GCSViews.ConfigurationView;

public partial class ConfigHWCANView : UserControl {
  public ConfigHWCANView() {
    InitializeComponent();
  }

  private void InitializeComponent() {
    AvaloniaXamlLoader.Load(this);
  }
}
