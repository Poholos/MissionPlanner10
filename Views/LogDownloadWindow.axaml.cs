using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

public partial class LogDownloadWindow : Window {
  public LogDownloadWindow() {
    AvaloniaXamlLoader.Load(this);
    DataContext = new LogDownloadViewModel();
  }

  public static void OpenWindow() {
    var w = new LogDownloadWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      w.Show(owner);
    } else {
      w.Show();
    }
  }
}
