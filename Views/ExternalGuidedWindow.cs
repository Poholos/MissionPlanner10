using Avalonia.Controls;
using Avalonia.Media;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

public sealed class ExternalGuidedWindow : Window {
  private readonly ExternalGuidedViewModel _viewModel = new();

  public ExternalGuidedWindow() {
    Title = "External Guided";
    Width = 620;
    Height = 390;
    MinWidth = 560;
    MinHeight = 350;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new ExternalGuidedView { DataContext = _viewModel };
    DataContext = _viewModel;
    Closed += async (_, _) => {
      await _viewModel.StopAsync();
      _viewModel.Dispose();
    };
  }

  public static void OpenWindow() {
    var window = new ExternalGuidedWindow();
    var owner = Services.Dialogs.Owner;
    if (owner != null) {
      window.Show(owner);
    } else {
      window.Show();
    }
  }
}
