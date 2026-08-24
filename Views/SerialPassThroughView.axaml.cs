using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MissionPlanner.Views;

public partial class SerialPassThroughView : UserControl {
  public SerialPassThroughView() {
    AvaloniaXamlLoader.Load(this);
  }
}
