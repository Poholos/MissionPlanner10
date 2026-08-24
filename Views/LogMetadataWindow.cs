using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MissionPlanner.Services;

namespace MissionPlanner.Views;

internal sealed class LogMetadataWindow : Window {
  private LogMetadataWindow(string title, DataGrid table, Control footer) {
    Title = title;
    Width = 760;
    Height = 560;
    MinWidth = 520;
    MinHeight = 320;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    Content = new Avalonia.Controls.Grid {
      Margin = new Thickness(12),
      RowDefinitions = new RowDefinitions("*,Auto"),
      RowSpacing = 8,
      Children = { table, footer },
    };
    Avalonia.Controls.Grid.SetRow(footer, 1);
  }

  internal static void ShowMessages(Window owner, IReadOnlyList<DataFlashMessage> messages) {
    var table = Table(messages);
    table.Columns.Add(TextColumn("Time (s)", nameof(DataFlashMessage.TimeText), 120));
    table.Columns.Add(TextColumn("Message", nameof(DataFlashMessage.Message), null));
    var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
    var window = new LogMetadataWindow($"Log Messages ({messages.Count})", table, close);
    close.Click += (_, _) => window.Close();
    window.Show(owner);
  }

  internal static void ShowParameters(
      Window owner, DataFlashParameterHistory history, string suggestedName) {
    var table = Table(history.Changes);
    table.Columns.Add(TextColumn("Time (s)", nameof(DataFlashParameterChange.TimeText), 100));
    table.Columns.Add(TextColumn("Parameter", nameof(DataFlashParameterChange.Name), null));
    table.Columns.Add(TextColumn("Value", nameof(DataFlashParameterChange.Value), 160));
    table.Columns.Add(TextColumn("Default", nameof(DataFlashParameterChange.DefaultValue), 160));
    var save = new Button { Content = "Save .param…" };
    var close = new Button { Content = "Close" };
    var buttons = new StackPanel {
      Orientation = Orientation.Horizontal,
      Spacing = 8,
      HorizontalAlignment = HorizontalAlignment.Right,
      Children = { save, close },
    };
    var window = new LogMetadataWindow(
        $"Log Parameter Changes ({history.Changes.Count})", table, buttons);
    close.Click += (_, _) => window.Close();
    save.Click += async (_, _) => {
      var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
        Title = "Save parameters from log",
        SuggestedFileName = suggestedName,
        DefaultExtension = "param",
        FileTypeChoices = [
          new FilePickerFileType("Parameter file") { Patterns = ["*.param", "*.parm"] },
        ],
      });
      if (file?.TryGetLocalPath() is { } path) {
        DataFlashLog.ExportParameters(history.FinalValues, path);
      }
    };
    window.Show(owner);
  }

  private static DataGrid Table<T>(IReadOnlyList<T> rows) => new() {
    ItemsSource = rows,
    AutoGenerateColumns = false,
    IsReadOnly = true,
    CanUserSortColumns = true,
    CanUserResizeColumns = true,
    GridLinesVisibility = DataGridGridLinesVisibility.All,
  };

  private static DataGridTextColumn TextColumn(string header, string property, double? width) =>
      new() {
        Header = header,
        Binding = new Binding(property),
        IsReadOnly = true,
        Width = width.HasValue
            ? new DataGridLength(width.Value)
            : new DataGridLength(1, DataGridLengthUnitType.Star),
      };
}
