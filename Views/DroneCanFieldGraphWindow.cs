using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MissionPlanner.Controls;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

public sealed class DroneCanFieldGraphWindow : Window, IDisposable {
  private readonly DroneCAN.DroneCAN _can;
  private readonly DroneCanGraphSelection _selection;
  private readonly int _history;
  private readonly object _gate = new();
  private readonly Dictionary<string, Queue<(double X, double Y)>> _series = [];
  private readonly Dictionary<string, ScottPlot.Color> _colors = [];
  private readonly Stopwatch _clock = Stopwatch.StartNew();
  private readonly DispatcherTimer _timer;
  private readonly LivePlot _plot = new();
  private bool _disposed;

  private static readonly ScottPlot.Color[] Palette = [
    ScottPlot.Colors.Red,
    ScottPlot.Colors.Green,
    ScottPlot.Colors.Blue,
    ScottPlot.Colors.Violet,
    ScottPlot.Colors.Orange,
    ScottPlot.Colors.Cyan,
  ];

  public DroneCanFieldGraphWindow(
      DroneCAN.DroneCAN can, DroneCanGraphSelection selection, int history) {
    _can = can;
    _selection = selection;
    _history = Math.Clamp(history, 10, 100000);
    Title = $"DroneCAN Graph — {selection.MessageName}.{selection.FieldName}";
    Width = 800;
    Height = 500;
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    _plot.SetAxisLabels("Time since graph opened (s)", "Value",
        $"Node {selection.NodeId}, message {selection.MessageId}");
    Content = _plot;

    _can.MessageReceived += OnMessage;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _timer.Tick += OnTick;
    _timer.Start();
    Closed += (_, _) => Dispose();
  }

  private void OnMessage(DroneCAN.CANFrame frame, object message, byte transferId) {
    if (frame.SourceNode != _selection.NodeId || frame.MsgTypeID != _selection.MessageId
        || !DroneCanGraphSampleExtractor.TryRead(
            message, _selection.FieldPath, out IReadOnlyList<double> values)) {
      return;
    }

    double x = _clock.Elapsed.TotalSeconds;
    lock (_gate) {
      for (int index = 0; index < values.Count; index++) {
        string label = values.Count == 1
            ? $"{_selection.MessageName}.{_selection.FieldName}"
            : $"{_selection.MessageName}.{_selection.FieldName}[{index}]";
        if (!_series.TryGetValue(label, out Queue<(double X, double Y)>? points)) {
          points = new Queue<(double X, double Y)>();
          _series[label] = points;
        }
        points.Enqueue((x, values[index]));
        while (points.Count > _history) {
          points.Dequeue();
        }
      }
    }
  }

  private void OnTick(object? sender, EventArgs args) {
    KeyValuePair<string, (double[] Xs, double[] Ys)>[] snapshot;
    lock (_gate) {
      snapshot = [.. _series.Select(pair => new KeyValuePair<string, (double[], double[])>(
          pair.Key,
          (pair.Value.Select(point => point.X).ToArray(),
           pair.Value.Select(point => point.Y).ToArray())))];
    }
    foreach (var pair in snapshot) {
      if (!_colors.TryGetValue(pair.Key, out ScottPlot.Color color)) {
        color = Palette[_colors.Count % Palette.Length];
        _colors[pair.Key] = color;
      }
      _plot.SetSeries(pair.Key, pair.Value.Xs, pair.Value.Ys, color);
    }
  }

  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _timer.Stop();
    _timer.Tick -= OnTick;
    _can.MessageReceived -= OnMessage;
  }
}

internal static class DroneCanGraphSampleExtractor {
  internal static bool IsSupportedType(Type type) {
    Type candidate = type.IsArray ? type.GetElementType() ?? type : type;
    return candidate.IsEnum || Type.GetTypeCode(candidate) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
        or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.Single
        or TypeCode.Double or TypeCode.Decimal;
  }

  internal static bool TryRead(
      object? message, IReadOnlyList<string> path, out IReadOnlyList<double> values) {
    values = [];
    if (message == null || path.Count == 0) {
      return false;
    }
    try {
      object? value = message;
      foreach (string segment in path) {
        if (value == null || !TryResolveSegment(value, segment, out value)) {
          return false;
        }
      }
      if (value is IEnumerable sequence and not string) {
        var converted = new List<double>();
        foreach (object? item in sequence) {
          if (!TryConvert(item, out double number)) {
            return false;
          }
          converted.Add(number);
        }
        values = converted;
        return converted.Count > 0;
      }
      if (!TryConvert(value, out double scalar)) {
        return false;
      }
      values = [scalar];
      return true;
    } catch {
      return false;
    }
  }

  private static bool TryResolveSegment(object source, string segment, out object? value) {
    value = null;
    int bracket = segment.LastIndexOf('[');
    string fieldName = bracket > 0 && segment.EndsWith(']')
        ? segment[..bracket] : segment;
    FieldInfo? field = source.GetType().GetField(fieldName);
    if (field == null) {
      return false;
    }
    value = field.GetValue(source);
    if (bracket <= 0) {
      return true;
    }
    if (value is not IList list
        || !int.TryParse(segment.AsSpan(bracket + 1, segment.Length - bracket - 2),
            NumberStyles.None, CultureInfo.InvariantCulture, out int index)
        || index < 0 || index >= list.Count) {
      return false;
    }
    value = list[index];
    return true;
  }

  private static bool TryConvert(object? value, out double number) {
    try {
      if (value is not IConvertible convertible) {
        number = 0;
        return false;
      }
      number = convertible.ToDouble(CultureInfo.InvariantCulture);
      return double.IsFinite(number);
    } catch {
      number = 0;
      return false;
    }
  }
}
