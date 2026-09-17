using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Avalonia.Threading;

namespace MissionPlanner.Controls;

public class LivePlot : ScottPlot.Avalonia.AvaPlot {
  internal long ContentRevision { get; private set; }
  private readonly Dictionary<string, ScottPlot.Plottables.Scatter> _series = new();
  private readonly Dictionary<string, List<double>> _appendXs = new();
  private readonly Dictionary<string, List<double>> _appendYs = new();

  public event Action<double>? PointClicked;

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    ContentRevision++;
    base.OnPointerPressed(e);
    if (PointClicked is not { } handler) {
      return;
    }
    var pos = e.GetPosition(this);

    var pixel = new ScottPlot.Pixel(pos.X * DisplayScale, pos.Y * DisplayScale);
    handler(Plot.GetCoordinates(pixel).X);
  }

  protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
    ContentRevision++; base.OnPointerWheelChanged(e);
  }

  public void SetSeries(string label, IReadOnlyList<double> xs, IReadOnlyList<double> ys,
                        ScottPlot.Color? color = null, bool rightAxis = false) {
    RunOnUi(() => {
      RemoveSeries(label);
      var scatter = Plot.Add.Scatter(xs.ToArray(), ys.ToArray(), color);
      scatter.LegendText = label;

      if (rightAxis) {
        scatter.Axes.YAxis = Plot.Axes.Right;
      }
      _series[label] = scatter;
      ContentRevision++;
      Refresh();
    });
  }

  public void RemoveByLabel(string label) {
    RunOnUi(() => {
      RemoveSeries(label);
      ContentRevision++;
      Refresh();
    });
  }

  public void AddVerticalLine(double x, ScottPlot.Color color, string? label = null) {
    RunOnUi(() => {
      var vl = Plot.Add.VerticalLine(x, 1, color);
      ContentRevision++;
      if (!string.IsNullOrEmpty(label)) {
        vl.LabelText = label;
      }
      Refresh();
    });
  }

  public IReadOnlyCollection<string> SeriesLabels => _series.Keys.ToList();

  public void AppendPoint(string label, double x, double y, int maxPoints = 2000) {
    RunOnUi(() => {
      if (!_appendXs.TryGetValue(label, out var xs)) {
        xs = new List<double>();
        _appendXs[label] = xs;
        _appendYs[label] = new List<double>();
      }
      var ys = _appendYs[label];
      xs.Add(x);
      ys.Add(y);
      while (xs.Count > maxPoints && xs.Count > 0) {
        xs.RemoveAt(0);
        ys.RemoveAt(0);
      }
      RemoveSeries(label);
      var scatter = Plot.Add.Scatter(xs.ToArray(), ys.ToArray());
      scatter.LegendText = label;
      _series[label] = scatter;
      ContentRevision++;
      Refresh();
    });
  }

  public void ClearAll() {
    RunOnUi(() => {
      Plot.Clear();
      ContentRevision++;
      _series.Clear();
      _appendXs.Clear();
      _appendYs.Clear();
      Refresh();
    });
  }

  public void SetAxisLabels(string xLabel, string yLabel, string? title = null) {
    RunOnUi(() => {
      Plot.XLabel(xLabel);
      Plot.YLabel(yLabel);
      if (title is not null) {
        Plot.Title(title);
      }
      Refresh();
    });
  }

  private void RemoveSeries(string label) {
    if (_series.TryGetValue(label, out var existing)) {
      Plot.Remove(existing);
      _series.Remove(label);
    }
  }

  private void RunOnUi(Action action) {
    if (CheckAccess()) {
      action();
    } else {
      Dispatcher.UIThread.Post(action);
    }
  }
}
