using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MissionPlanner.Views;

public partial class MavlinkSerialTcpBridgeView : UserControl {
  public MavlinkSerialTcpBridgeView() => AvaloniaXamlLoader.Load(this);
}
