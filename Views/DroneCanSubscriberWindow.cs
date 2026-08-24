using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Views;

public sealed class DroneCanSubscriberWindow : Window, IDisposable {
  private readonly DroneCAN.DroneCAN _can;
  private readonly ObservableCollection<string> _messageTypes = [];
  private readonly ConcurrentDictionary<string, byte> _seenTypes = new(StringComparer.Ordinal);
  private readonly ConcurrentQueue<(string Type, object Message)> _pending = new();
  private readonly List<string> _lines = [];
  private readonly ComboBox _messageType;
  private readonly NumericUpDown _lineLimit;
  private readonly TextBox _output;
  private readonly DispatcherTimer _timer;
  private string? _selectedMessageType;
  private int _pendingCount;
  private int _droppedMessages;
  private bool _disposed;

  private static readonly JsonSerializerOptions JsonOptions = new() {
    IncludeFields = true,
    WriteIndented = true,
  };

  public DroneCanSubscriberWindow(
      DroneCAN.DroneCAN can, DroneCanMessageSelection selection) {
    _can = can;
    Title = $"DroneCAN Subscriber — node {selection.NodeId}";
    Width = 720;
    Height = 520;
    Background = new SolidColorBrush(Color.Parse("#434445"));
    WindowStartupLocation = WindowStartupLocation.CenterOwner;

    _messageTypes.Add(selection.MessageName);
    _seenTypes.TryAdd(selection.MessageName, 0);
    _messageType = new ComboBox {
      Width = 320,
      ItemsSource = _messageTypes,
      SelectedItem = selection.MessageName,
    };
    _selectedMessageType = selection.MessageName;
    _messageType.SelectionChanged += (_, _) =>
        Volatile.Write(ref _selectedMessageType, _messageType.SelectedItem as string);
    _lineLimit = new NumericUpDown {
      Minimum = 1,
      Maximum = 10000,
      Value = 100,
      Width = 90,
      FormatString = "0",
    };
    _output = new TextBox {
      AcceptsReturn = true,
      IsReadOnly = true,
      TextWrapping = TextWrapping.NoWrap,
      FontFamily = new FontFamily("Courier New,monospace"),
    };
    _output.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
        Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
    _output.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
        Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
    var toolbar = new StackPanel {
      Orientation = Orientation.Horizontal,
      Spacing = 8,
      Children = {
        new TextBlock { Text = "Message:", VerticalAlignment = VerticalAlignment.Center },
        _messageType,
        new TextBlock { Text = "Lines:", VerticalAlignment = VerticalAlignment.Center },
        _lineLimit,
      },
    };
    Content = new Avalonia.Controls.Grid {
      Margin = new Thickness(8),
      RowDefinitions = new RowDefinitions("Auto,*"),
      Children = { toolbar, _output },
    };
    Avalonia.Controls.Grid.SetRow(_output, 1);
    _output.Margin = new Thickness(0, 8, 0, 0);

    _can.MessageReceived += OnMessage;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    _timer.Tick += OnTick;
    _timer.Start();
    Closed += (_, _) => Dispose();
  }

  private void OnMessage(DroneCAN.CANFrame frame, object message, byte transferId) {
    string type = message.GetType().Name;
    _seenTypes.TryAdd(type, 0);
    string? selected = Volatile.Read(ref _selectedMessageType);
    if (!string.Equals(type, selected, StringComparison.Ordinal)) {
      return;
    }
    if (Volatile.Read(ref _pendingCount) >= 100) {
      Interlocked.Increment(ref _droppedMessages);
      return;
    }
    _pending.Enqueue((type, message));
    Interlocked.Increment(ref _pendingCount);
  }

  private void OnTick(object? sender, EventArgs args) {
    bool changed = false;
    foreach (string type in _seenTypes.Keys.OrderBy(value => value, StringComparer.Ordinal)) {
      if (!_messageTypes.Contains(type)) {
        _messageTypes.Add(type);
        changed = true;
      }
    }

    int limit = Math.Clamp((int)(_lineLimit.Value ?? 100), 1, 10000);
    int processed = 0;
    while (processed < 20 && _pending.TryDequeue(out var pending)) {
      Interlocked.Decrement(ref _pendingCount);
      string json;
      try {
        json = JsonSerializer.Serialize(pending.Message, pending.Message.GetType(), JsonOptions);
      } catch (Exception ex) {
        json = $"<{pending.Type}: serialization failed: {ex.Message}>";
      }
      AppendBoundedLines(_lines, json, limit);
      changed = true;
      processed++;
    }
    int dropped = Interlocked.Exchange(ref _droppedMessages, 0);
    if (dropped > 0) {
      AppendBoundedLines(_lines,
          $"<dropped {dropped} messages while the subscriber UI was busy>", limit);
      changed = true;
    }
    while (_lines.Count > limit) {
      _lines.RemoveAt(0);
      changed = true;
    }
    if (!changed) {
      return;
    }
    _output.Text = string.Join(Environment.NewLine, _lines);
    _output.CaretIndex = _output.Text?.Length ?? 0;
  }

  internal static void AppendBoundedLines(List<string> destination, string text, int maximum) {
    destination.AddRange(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    int excess = destination.Count - Math.Max(1, maximum);
    if (excess > 0) {
      destination.RemoveRange(0, excess);
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
