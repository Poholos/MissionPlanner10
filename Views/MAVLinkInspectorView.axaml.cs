using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MissionPlanner.Views;

public partial class MAVLinkInspectorView : UserControl {
  public MAVLinkInspectorView() {
    AvaloniaXamlLoader.Load(this);
  }
}
