using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

public partial class ConnectionOptionsWindow : Window {
  public ConnectionOptionsWindow() {
    AvaloniaXamlLoader.Load(this);
    DataContext = new ConnectionOptionsViewModel();
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close();

  public static void OpenWindow() {
    var w = new ConnectionOptionsWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      w.Show(owner);
    } else {
      w.Show();
    }
  }
}
