using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MissionPlanner.Views.GCSViews.ConfigurationView;

public partial class ConfigFrameTypeView : UserControl {
  public ConfigFrameTypeView() {
    InitializeComponent();
  }

  private void InitializeComponent() {
    AvaloniaXamlLoader.Load(this);
  }
}
