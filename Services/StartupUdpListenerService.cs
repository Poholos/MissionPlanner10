using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

namespace MissionPlanner.Services;

internal sealed record StartupUdpListenerOptions(
    bool Enabled,
    int PrimaryPort,
    int AlternatePort) {
  internal const bool DefaultEnabled = true;
  internal const int DefaultPrimaryPort = 14550;
  internal const int DefaultAlternatePort = 14551;

  internal static StartupUdpListenerOptions Default { get; } = new(
      DefaultEnabled, DefaultPrimaryPort, DefaultAlternatePort);

  internal IReadOnlyList<int> Ports => Enabled
      ? new[] { NormalizePort(PrimaryPort, DefaultPrimaryPort),
                NormalizePort(AlternatePort, DefaultAlternatePort) }
          .Distinct()
          .ToArray()
      : [];

  internal static StartupUdpListenerOptions Load(Settings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    return new StartupUdpListenerOptions(
        settings.GetBoolean(
            StartupUdpListenerService.EnabledSettingKey, DefaultEnabled),
        NormalizePort(
            settings.GetInt32(
                StartupUdpListenerService.PrimaryPortSettingKey, DefaultPrimaryPort),
            DefaultPrimaryPort),
        NormalizePort(
            settings.GetInt32(
                StartupUdpListenerService.AlternatePortSettingKey, DefaultAlternatePort),
            DefaultAlternatePort));
  }

  internal static int NormalizePort(int value, int fallback) =>
      value is >= 1 and <= 65535 ? value : fallback;
}

internal sealed record StartupUdpListenerStartResult(
    bool Enabled,
    IReadOnlyList<int> RequestedPorts,
    IReadOnlyList<MavLinkConnection> Opened,
    IReadOnlyList<ConnectionListOpenFailure> Failures) {
  internal string Status {
    get {
      if (!Enabled) {
        return "Automatic startup UDP listeners are disabled.";
      }
      string ports = string.Join(", ", Opened.Select(item =>
          item.Source!.Port.ToString(CultureInfo.InvariantCulture)));
      if (Failures.Count == 0) {
        return $"Listening for independent MAVLink connections on UDP {ports}.";
      }
      string errors = string.Join("; ", Failures.Select(failure =>
          $"UDP {failure.Endpoint.Port}: {failure.Message}"));
      return Opened.Count == 0
          ? $"Could not open startup UDP listeners: {errors}"
          : $"Listening on UDP {ports}. Some listeners failed: {errors}";
    }
  }
}

/// <summary>
/// Restores Mission Planner's default inbound MAVLink listeners without starting the broader
/// AutoConnect video-port catalogue. Every UDP port owns a separate MAVLink interface/runtime so
/// vehicles arriving on 14550 and 14551 remain independently selectable and usable.
/// </summary>
internal static class StartupUdpListenerService {
  internal const string EnabledSettingKey = "startup_udp_listeners_enabled";
  internal const string PrimaryPortSettingKey = "startup_udp_primary_port";
  internal const string AlternatePortSettingKey = "startup_udp_alternate_port";

  private static readonly object _startGate = new();
  private static StartupUdpListenerStartResult? _startupResult;

  internal static string Status => _startupResult?.Status ??
      "UDP listener changes take effect after restarting Mission Planner.";

  internal static StartupUdpListenerStartResult StartFromSettings(
      MavLinkConnectionManager manager) {
    ArgumentNullException.ThrowIfNull(manager);
    lock (_startGate) {
      return _startupResult ??= Start(
          StartupUdpListenerOptions.Load(Settings.Instance), manager,
          openTelemetryLogsOnFirstTraffic: true);
    }
  }

  internal static StartupUdpListenerStartResult Start(
      StartupUdpListenerOptions options,
      MavLinkConnectionManager manager,
      bool openTelemetryLogsOnFirstTraffic = false) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(manager);

    IReadOnlyList<int> ports = options.Ports;
    var opened = new List<MavLinkConnection>(ports.Count);
    var failures = new List<ConnectionListOpenFailure>();
    int sourceLine = 0;
    foreach (int port in ports) {
      sourceLine++;
      var endpoint = new ConnectionListEndpoint(
          ConnectionListTransport.UdpListener,
          "0.0.0.0", port, "", 0, sourceLine);
      if (manager.ContainsEndpoint(endpoint.Canonical)) {
        failures.Add(new ConnectionListOpenFailure(
            endpoint, "Connection is already open."));
        continue;
      }

      MAVLinkInterface? link = null;
      ICommsSerial? stream = null;
      try {
        stream = new UdpSerial(UdpSerial.CreateSharedListener(port)) {
          Port = port.ToString(CultureInfo.InvariantCulture),
        };
        link = new MAVLinkInterface { BaseStream = stream };
        ViewModels.ConnectionViewModel.ResetAllVehicleParameters(link);
        MavLinkConnection connection = manager.Add(
            link, endpoint,
            item => new MavLinkSecondaryRuntime(
                item, manager.NotifyClosed,
                openTelemetryLogsOnFirstTraffic: openTelemetryLogsOnFirstTraffic));
        opened.Add(connection);
        link = null;
        stream = null;
      } catch (Exception ex) {
        CloseUnregistered(link, stream);
        failures.Add(new ConnectionListOpenFailure(endpoint, UserMessage(ex)));
      }
    }

    return new StartupUdpListenerStartResult(
        options.Enabled, ports, opened, failures);
  }

  private static void CloseUnregistered(MAVLinkInterface? link, ICommsSerial? stream) {
    if (link != null) {
      MavLinkConnectionManager.SafeClose(link);
      return;
    }
    try {
      stream?.Close();
    } catch {
    }
    (stream as IDisposable)?.Dispose();
  }

  private static string UserMessage(Exception exception) {
    Exception current = exception;
    while (current.InnerException != null &&
           current is AggregateException or System.Reflection.TargetInvocationException) {
      current = current.InnerException;
    }
    return string.IsNullOrWhiteSpace(current.Message)
        ? current.GetType().Name
        : current.Message;
  }
}
