using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

internal sealed class ParamCompareWindow : Window {
  private ParamCompareWindow(
      IReadOnlyList<IParameterComparisonRow> rows,
      string title,
      string proposedHeader,
      string instructions,
      string acceptText) {
    Title = title;
    Width = 720;
    Height = 560;
    MinWidth = 520;
    MinHeight = 360;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    var grid = new DataGrid {
      ItemsSource = rows,
      AutoGenerateColumns = false,
      CanUserSortColumns = true,
      CanUserResizeColumns = true,
      GridLinesVisibility = DataGridGridLinesVisibility.All,
      IsReadOnly = false,
    };
    grid.Columns.Add(new DataGridCheckBoxColumn {
      Header = "Use",
      Binding = new Binding(nameof(ParamComparisonRow.Use)) { Mode = BindingMode.TwoWay },
      Width = new DataGridLength(60),
    });
    grid.Columns.Add(new DataGridTextColumn {
      Header = "Parameter",
      Binding = new Binding(nameof(ParamComparisonRow.Name)),
      IsReadOnly = true,
      Width = new DataGridLength(1, DataGridLengthUnitType.Star),
    });
    grid.Columns.Add(new DataGridTextColumn {
      Header = "Current",
      Binding = new Binding(nameof(ParamComparisonRow.CurrentText)),
      IsReadOnly = true,
      Width = new DataGridLength(160),
    });
    grid.Columns.Add(new DataGridTextColumn {
      Header = proposedHeader,
      Binding = new Binding(nameof(IParameterComparisonRow.ProposedText)),
      IsReadOnly = true,
      Width = new DataGridLength(160),
    });

    var toggleAll = new CheckBox { Content = "Use all", IsChecked = true };
    toggleAll.IsCheckedChanged += (_, _) => {
      bool use = toggleAll.IsChecked == true;
      foreach (var row in rows) {
        row.Use = use;
      }
    };
    var cancel = new Button { Content = "Cancel" };
    var stage = new Button { Content = acceptText, IsDefault = true };
    cancel.Click += (_, _) => Close(false);
    stage.Click += (_, _) => Close(true);

    Content = new Avalonia.Controls.Grid {
      Margin = new Thickness(12),
      RowDefinitions = new RowDefinitions("Auto,*,Auto"),
      RowSpacing = 10,
      Children = {
        new TextBlock {
          Text = instructions,
          TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        },
        grid,
        new Avalonia.Controls.Grid {
          ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
          ColumnSpacing = 8,
          Children = { toggleAll, cancel, stage },
        },
      },
    };
    Avalonia.Controls.Grid.SetRow(grid, 1);
    Avalonia.Controls.Grid.SetRow((Content as Avalonia.Controls.Grid)!.Children[2], 2);
    Avalonia.Controls.Grid.SetColumn(cancel, 1);
    Avalonia.Controls.Grid.SetColumn(stage, 2);
  }

  internal static Task<bool> ShowAsync(
      Window owner, IReadOnlyList<ParamComparisonRow> rows) =>
      new ParamCompareWindow(
          rows,
          "Compare Parameters",
          "File",
          "Choose which file values to stage. Nothing is written to the vehicle until Write Params is used.",
          "Stage selected").ShowDialog<bool>(owner);

  internal static Task<bool> ShowAsync(
      Window owner,
      IReadOnlyList<IParameterComparisonRow> rows,
      string title,
      string proposedHeader,
      string instructions) =>
      new ParamCompareWindow(
          rows, title, proposedHeader, instructions, "Stage selected").ShowDialog<bool>(owner);
}
