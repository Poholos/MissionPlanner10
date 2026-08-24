using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MissionPlanner.Controls;
using MissionPlanner.Services;
using MissionPlanner.ViewModels.Setup;

namespace MissionPlanner.Views.Setup;

public partial class SikRadioView : UserControl {
  private static readonly FilePickerFileType ProfileFiles = new("SiK/RFD settings profile") {
    Patterns = ["*.ini", "*.txt", "*.cfg"],
  };

  private SikRadioViewModel? _vm;
  private readonly LivePlot? _plot;

  public SikRadioView() {
    InitializeComponent();
    _plot = this.FindControl<LivePlot>("RssiPlot");
    DataContextChanged += OnDataContextChanged;
    this.FindControl<Button>("SaveLocalProfileButton")!.Click += async (_, _) =>
        await SaveProfile(remote: false);
    this.FindControl<Button>("LoadLocalProfileButton")!.Click += async (_, _) =>
        await LoadProfile(remote: false);
    this.FindControl<Button>("SaveRemoteProfileButton")!.Click += async (_, _) =>
        await SaveProfile(remote: true);
    this.FindControl<Button>("LoadRemoteProfileButton")!.Click += async (_, _) =>
        await LoadProfile(remote: true);
  }

  private void InitializeComponent() {
    AvaloniaXamlLoader.Load(this);
  }

  private void OnDataContextChanged(object? sender, EventArgs e) {
    if (_vm != null) {
      _vm.RssiSample -= OnRssiSample;
      _vm.RssiReset -= OnRssiReset;
    }

    _vm = DataContext as SikRadioViewModel;

    if (_vm != null) {
      _vm.RssiSample += OnRssiSample;
      _vm.RssiReset += OnRssiReset;
      _plot?.SetAxisLabels("Time (s)", "RSSI / Noise", "Live RSSI");
    }
  }

  private void OnRssiSample(double t, double rssiL, double rssiR, double noiseL, double noiseR) {
    if (_plot == null) {
      return;
    }
    _plot.AppendPoint("RSSI Local", t, rssiL);
    _plot.AppendPoint("RSSI Remote", t, rssiR);
    _plot.AppendPoint("Noise Local", t, noiseL);
    _plot.AppendPoint("Noise Remote", t, noiseR);
  }

  private void OnRssiReset() {
    _plot?.ClearAll();
    _plot?.SetAxisLabels("Time (s)", "RSSI / Noise", "Live RSSI");
  }

  private async System.Threading.Tasks.Task SaveProfile(bool remote) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || _vm == null) {
      return;
    }
    IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = remote ? "Save remote SiK/RFD settings" : "Save local SiK/RFD settings",
      SuggestedFileName = _vm.SuggestedProfileFileName(remote),
      DefaultExtension = "ini",
      FileTypeChoices = [ProfileFiles],
    });
    string? path = file?.TryGetLocalPath();
    if (path == null || !await Dialogs.ConfirmDangerous(
            "Export SiK/RFD settings",
            "The profile can include an encryption key, network ID and radio frequencies. "
                + "Save it only to a trusted location and review it before sharing.",
            "EXPORT PROFILE")) {
      return;
    }
    try {
      await File.WriteAllTextAsync(path, _vm.ExportProfile(remote));
    } catch (Exception ex) {
      await Dialogs.Alert("Export SiK/RFD settings", ex.Message);
    }
  }

  private async System.Threading.Tasks.Task LoadProfile(bool remote) {
    TopLevel? top = TopLevel.GetTopLevel(this);
    if (top == null || _vm == null) {
      return;
    }
    var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = remote ? "Load remote SiK/RFD settings" : "Load local SiK/RFD settings",
      AllowMultiple = false,
      FileTypeFilter = [ProfileFiles, new FilePickerFileType("All files") { Patterns = ["*"] }],
    });
    string? path = files.FirstOrDefault()?.TryGetLocalPath();
    if (path == null) {
      return;
    }
    try {
      var result = _vm.ImportProfile(await File.ReadAllTextAsync(path), remote);
      await Dialogs.Alert("Load SiK/RFD settings",
          $"Staged {result.Applied} value(s). Unknown: {result.Unknown}; invalid: "
              + $"{result.Invalid}; ignored lines: {result.Ignored}. Nothing was sent to the radio; "
              + "review the values and press Save Settings to apply them.");
    } catch (Exception ex) {
      await Dialogs.Alert("Load SiK/RFD settings", ex.Message);
    }
  }
}
