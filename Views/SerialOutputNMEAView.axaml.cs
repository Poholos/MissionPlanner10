using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MissionPlanner.Views;

public partial class SerialOutputNMEAView : UserControl {
  public SerialOutputNMEAView() {
    AvaloniaXamlLoader.Load(this);
  }
}
