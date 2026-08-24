using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MissionPlanner.Services;

namespace MissionPlanner.Views;

internal sealed class OsdTuningSlotsWindow : Window {
  private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

  private readonly MAVLinkInterface _comPort;
  private readonly DataGrid _grid;
  private readonly TextBlock _status;
  private readonly Button _reload;
  private readonly Button _save;
  private readonly List<OsdTuningSlotRow> _rows = [];
  private readonly CancellationTokenSource _lifetime = new();
  private OsdTuningSlotService? _service;
  private bool _busy;

  private OsdTuningSlotsWindow(MAVLinkInterface comPort) {
    _comPort = comPort;
    Title = "OSD 5/6 Tuning Slots";
    Width = 1080;
    Height = 650;
    MinWidth = 760;
    MinHeight = 420;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    _grid = BuildGrid();
    _status = new TextBlock {
      Text = "Opening MAVLink tuning-slot editor…",
      TextWrapping = TextWrapping.Wrap,
      VerticalAlignment = VerticalAlignment.Center,
    };
    _reload = new Button { Content = "Reload", MinWidth = 90 };
    _save = new Button { Content = "Write Changes", MinWidth = 120, IsDefault = true };
    var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
    _reload.Click += async (_, _) => await LoadAsync();
    _save.Click += async (_, _) => await SaveAsync();
    close.Click += (_, _) => Close();

    var buttons = new Avalonia.Controls.Grid {
      ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
      ColumnSpacing = 8,
      Children = { _status, _reload, _save, close },
    };
    Avalonia.Controls.Grid.SetColumn(_reload, 1);
    Avalonia.Controls.Grid.SetColumn(_save, 2);
    Avalonia.Controls.Grid.SetColumn(close, 3);

    Content = new Avalonia.Controls.Grid {
      Margin = new Thickness(14),
      RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
      RowSpacing = 10,
      Children = {
        new TextBlock {
          Text = "ArduPilot screens 5 and 6 expose nine MAVLink parameter-editor slots each. "
              + "Type is 0=None/manual, 1=Serial protocol, 2=Servo function, 3=Aux function, "
              + "4=Flight mode, 5/6/7=firmware-defined failsafe presets.",
          TextWrapping = TextWrapping.Wrap,
        },
        new TextBlock {
          Text = "Parameter IDs are printable ASCII and at most 16 bytes. Min/Max/Step are used "
              + "for manual type 0; preset types let the firmware choose their option range.",
          Opacity = 0.75,
          TextWrapping = TextWrapping.Wrap,
        },
        _grid,
        buttons,
      },
    };
    var root = (Avalonia.Controls.Grid)Content;
    Avalonia.Controls.Grid.SetRow(root.Children[1], 1);
    Avalonia.Controls.Grid.SetRow(_grid, 2);
    Avalonia.Controls.Grid.SetRow(buttons, 3);

    Opened += async (_, _) => await LoadAsync();
    Closing += (_, _) => {
      _lifetime.Cancel();
      _service?.Dispose();
      _service = null;
    };
  }

  internal static Task ShowAsync(Window owner, MAVLinkInterface comPort) =>
      new OsdTuningSlotsWindow(comPort).ShowDialog(owner);

  private DataGrid BuildGrid() {
    var result = new DataGrid {
      AutoGenerateColumns = false,
      CanUserResizeColumns = true,
      CanUserSortColumns = false,
      GridLinesVisibility = DataGridGridLinesVisibility.All,
      IsReadOnly = false,
    };
    result.Columns.Add(Column("Screen", nameof(OsdTuningSlotRow.Screen), 65, true));
    result.Columns.Add(Column("Slot", nameof(OsdTuningSlotRow.Index), 55, true));
    result.Columns.Add(Column("Parameter", nameof(OsdTuningSlotRow.ParameterName), 220, false));
    result.Columns.Add(Column("Type 0..7", nameof(OsdTuningSlotRow.TypeText), 100, false));
    result.Columns.Add(Column("Minimum", nameof(OsdTuningSlotRow.MinimumText), 120, false));
    result.Columns.Add(Column("Maximum", nameof(OsdTuningSlotRow.MaximumText), 120, false));
    result.Columns.Add(Column("Step", nameof(OsdTuningSlotRow.IncrementText), 120, false));
    result.Columns.Add(Column("Result", nameof(OsdTuningSlotRow.Result), 190, true));
    return result;
  }

  private static DataGridTextColumn Column(
      string header, string property, double width, bool readOnly) => new() {
        Header = header,
        Binding = new Binding(property) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay },
        IsReadOnly = readOnly,
        Width = new DataGridLength(width),
      };

  private async Task LoadAsync() {
    if (_busy) {
      return;
    }
    SetBusy(true, "Reading 18 slots from the selected MAVLink device…");
    try {
      _service?.Dispose();
      _service = new OsdTuningSlotService(_comPort);
      IReadOnlyList<OsdTuningSlot> slots = await _service.ReadAllAsync(
          RequestTimeout, _lifetime.Token);
      _rows.Clear();
      _rows.AddRange(slots.OrderBy(slot => slot.Screen).ThenBy(slot => slot.Index)
          .Select(slot => new OsdTuningSlotRow(slot)));
      _grid.ItemsSource = null;
      _grid.ItemsSource = _rows;
      _status.Text = $"Loaded {_rows.Count} slots from system {_service.SystemId}, "
          + $"component {_service.ComponentId}.";
    } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
    } catch (TimeoutException) {
      _status.Text = "The device did not answer an OSD tuning-slot request within 3 seconds. "
          + "No values were changed.";
    } catch (Exception ex) {
      _status.Text = "Unable to read OSD tuning slots: " + ex.Message;
    } finally {
      SetBusy(false);
    }
  }

  private async Task SaveAsync() {
    if (_busy || _service == null) {
      return;
    }
    _grid.CommitEdit(DataGridEditingUnit.Row, true);
    var changed = _rows.Where(row => row.Changed).ToArray();
    if (changed.Length == 0) {
      _status.Text = "No tuning-slot changes to write.";
      return;
    }

    var parsed = new List<(OsdTuningSlotRow Row, OsdTuningSlot Slot)>();
    foreach (OsdTuningSlotRow row in changed) {
      if (!row.TryBuild(out OsdTuningSlot? slot, out string error)) {
        row.Result = error;
        _grid.ItemsSource = null;
        _grid.ItemsSource = _rows;
        _status.Text = $"Screen {row.Screen}, slot {row.Index}: {error}";
        return;
      }
      parsed.Add((row, slot!));
    }

    SetBusy(true, $"Writing {parsed.Count} changed slot(s)…");
    int completed = 0;
    try {
      foreach (var (row, slot) in parsed) {
        OsdTuningWriteResult result = await _service.WriteAsync(
            slot, RequestTimeout, _lifetime.Token);
        row.Result = result.Success ? "Written" : result.Result.ToString();
        if (!result.Success) {
          throw new InvalidOperationException(
              $"Screen {row.Screen}, slot {row.Index}: {result.Result}.");
        }
        row.Accept(slot);
        completed++;
      }
      _status.Text = $"Wrote {completed} tuning-slot change(s).";
    } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
    } catch (TimeoutException) {
      _status.Text = $"Device stopped answering after {completed} of {parsed.Count} update(s). "
          + "Further writes were stopped.";
    } catch (Exception ex) {
      _status.Text = $"Stopped after {completed} of {parsed.Count} update(s): {ex.Message}";
    } finally {
      _grid.ItemsSource = null;
      _grid.ItemsSource = _rows;
      SetBusy(false);
    }
  }

  private void SetBusy(bool value, string? status = null) {
    _busy = value;
    _reload.IsEnabled = !value;
    _save.IsEnabled = !value;
    _grid.IsEnabled = !value;
    if (status != null) {
      _status.Text = status;
    }
  }
}

internal sealed class OsdTuningSlotRow {
  private OsdTuningSlot _accepted;

  internal OsdTuningSlotRow(OsdTuningSlot slot) {
    _accepted = slot;
    Screen = slot.Screen;
    Index = slot.Index;
    Copy(slot);
    Result = "Loaded";
  }

  public byte Screen { get; }
  public byte Index { get; }
  public string ParameterName { get; set; } = "";
  public string TypeText { get; set; } = "0";
  public string MinimumText { get; set; } = "0";
  public string MaximumText { get; set; } = "0";
  public string IncrementText { get; set; } = "0";
  public string Result { get; set; } = "";

  internal bool Changed => !string.Equals(ParameterName?.Trim(), _accepted.ParameterName,
                               StringComparison.Ordinal)
      || !byte.TryParse(TypeText, NumberStyles.None, CultureInfo.InvariantCulture, out byte type)
      || type != (byte)_accepted.Type
      || !Equal(MinimumText, _accepted.Minimum)
      || !Equal(MaximumText, _accepted.Maximum)
      || !Equal(IncrementText, _accepted.Increment);

  internal bool TryBuild(out OsdTuningSlot? slot, out string error) {
    slot = null;
    if (!byte.TryParse(TypeText, NumberStyles.None, CultureInfo.InvariantCulture, out byte type)
        || type >= (byte)MAVLink.OSD_PARAM_CONFIG_TYPE.OSD_PARAM_NUM_TYPES) {
      error = "Type must be 0..7.";
      return false;
    }
    if (!TryFloat(MinimumText, out float minimum)
        || !TryFloat(MaximumText, out float maximum)
        || !TryFloat(IncrementText, out float increment)) {
      error = "Minimum, maximum and step must be finite numbers.";
      return false;
    }
    if (type == 0 && (maximum < minimum || increment < 0)) {
      error = "Manual type requires maximum >= minimum and a non-negative step.";
      return false;
    }
    try {
      _ = OsdTuningSlotService.EncodeParameterId(ParameterName);
    } catch (ArgumentException ex) {
      error = ex.Message;
      return false;
    }
    slot = new OsdTuningSlot(Screen, Index, ParameterName.Trim(),
        (MAVLink.OSD_PARAM_CONFIG_TYPE)type, minimum, maximum, increment);
    error = "";
    return true;
  }

  internal void Accept(OsdTuningSlot slot) {
    _accepted = slot;
    Copy(slot);
  }

  private void Copy(OsdTuningSlot slot) {
    ParameterName = slot.ParameterName;
    TypeText = ((byte)slot.Type).ToString(CultureInfo.InvariantCulture);
    MinimumText = Format(slot.Minimum);
    MaximumText = Format(slot.Maximum);
    IncrementText = Format(slot.Increment);
  }

  private static string Format(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

  private static bool Equal(string text, float expected) =>
      TryFloat(text, out float value) && value.Equals(expected);

  private static bool TryFloat(string? text, out float value) =>
      float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
      && float.IsFinite(value);
}
