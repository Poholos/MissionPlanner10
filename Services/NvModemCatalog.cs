using System;
using System.Collections.Generic;
using System.Linq;

namespace MissionPlanner.Services;

internal enum NvModemGeneration {
  Unknown,
  Nv4,
  Nv5,
}

internal static class NvModemCatalog {
  internal const int Nv5KeyBytes = 16;
  internal const int Nv5KeyWordBytes = 4;
  internal const int Nv5KeyWordCount = Nv5KeyBytes / Nv5KeyWordBytes;
  internal const int Nv4KeyBytes = 32;
  internal const int Nv5MinimumFrameBytes = 64;
  internal const int Nv5MaximumFrameBytes = 496;
  internal const int Nv5FrameStepBytes = 16;

  internal static IReadOnlyList<string> Nv4ParameterNames { get; } = Array.AsReadOnly(new[] {
    "HW_VERSION", "WD_TIMEOUT", "DATA_REFLECT", "DATA_RF_STAT",
    "RC_DELAY", "RX_RSSI_TYPE", "SBUS_ENABLE", "SBUS_MASK",
    "LOCAL_SYS_ID", "LOCAL_COMP_ID", "UAV_SYS_ID", "UAV_COMP_ID",
    "NET_PORT_LOCAL", "NET_PORT_REMOTE", "PROXY_UDP_RPORT", "PROXY_UDP_LPORT",
    "PROXY_RSSI", "NET_ENABLE", "TX_ON", "SERIAL_BAUDRATE", "UNITED_PKG_CNT",
    "USE_FHSS", "GUARD_INTERVAL", "CENTRAL_FREQ_MZ", "BANDWIDTH_MHZ", "PREAMBLE_LEN",
    "ENC_KEY_BYTE1", "ENC_KEY_BYTE2", "ENC_KEY_BYTE3", "ENC_KEY_BYTE4",
    "ENC_KEY_BYTE5", "ENC_KEY_BYTE6", "ENC_KEY_BYTE7", "ENC_KEY_BYTE8",
    "CHL_WIDE_KHZ", "ENC_KEY_BITS", "SPREAD_FACTOR", "POWER_TX", "LNA_GAIN",
    "CODING_RATE", "HOPS_WAITING", "SYNC_WORD", "HARDWARE_CRC",
    "NET_BYTE_1", "NET_BYTE_2", "NET_BYTE_3", "NET_MASK_1", "NET_MASK_2",
    "NET_MASK_3", "NET_MASK_4", "NET_BYTE_REMOTE", "CHECK_SYNC_WORD",
    "ACCEPT_UNKN_MAV", "UART2_STAT_ON", "DEV_MODE", "REFRESH_SETTING",
  });

  internal static bool IsNv5Signature(string name) =>
      name is "MODEM_PROFILE" or "RADIO_COUNT"
      || name.StartsWith("RTSP_", StringComparison.Ordinal)
      || name.StartsWith("CH1_", StringComparison.Ordinal)
      || name.StartsWith("CH2_", StringComparison.Ordinal);

  internal static bool IsNv4Signature(string name) =>
      name is "HW_VERSION" or "CENTRAL_FREQ_MZ"
      || IsNv4RefreshParameter(name)
      || Nv4KeyWordIndex(name) >= 0;

  internal static bool IsNv4RefreshParameter(string name) =>
      name is "REFRESH_SETTING" or "REFRESH_SETTINGS";

  internal static string? Nv4RefreshParameterName(
      IReadOnlyDictionary<string, double> parameters) {
    if (parameters.ContainsKey("REFRESH_SETTING")) {
      return "REFRESH_SETTING";
    }
    if (parameters.ContainsKey("REFRESH_SETTINGS")) {
      return "REFRESH_SETTINGS";
    }
    return null;
  }

  internal static string Nv5KeyWordName(int channel, int word) =>
      $"CH{channel}_KEY_W{word}";

  internal static int Nv5KeyWordIndex(string name) {
    string prefix = name.StartsWith("CH1_", StringComparison.Ordinal)
        ? "CH1_KEY_W"
        : name.StartsWith("CH2_", StringComparison.Ordinal) ? "CH2_KEY_W" : "";
    return prefix.Length != 0 && name.Length == prefix.Length + 1
        && int.TryParse(name.AsSpan(prefix.Length), out int word)
        && word is >= 0 and < Nv5KeyWordCount ? word : -1;
  }

  internal static uint Nv5KeyWord(ReadOnlySpan<byte> key, int word) {
    int offset = checked(word * Nv5KeyWordBytes);
    if (word is < 0 or >= Nv5KeyWordCount || offset + Nv5KeyWordBytes > key.Length) {
      throw new ArgumentOutOfRangeException(nameof(word));
    }
    return ((uint)key[offset] << 24)
        | ((uint)key[offset + 1] << 16)
        | ((uint)key[offset + 2] << 8)
        | key[offset + 3];
  }

  internal static int Nv5SignedKeyWord(ReadOnlySpan<byte> key, int word) =>
      unchecked((int)Nv5KeyWord(key, word));

  internal static void WriteNv5KeyWord(Span<byte> key, int word, uint value) {
    int offset = checked(word * Nv5KeyWordBytes);
    if (word is < 0 or >= Nv5KeyWordCount || offset + Nv5KeyWordBytes > key.Length) {
      throw new ArgumentOutOfRangeException(nameof(word));
    }
    key[offset] = (byte)(value >> 24);
    key[offset + 1] = (byte)(value >> 16);
    key[offset + 2] = (byte)(value >> 8);
    key[offset + 3] = (byte)value;
  }

  internal static int Nv4KeyWordIndex(string name) {
    const string prefix = "ENC_KEY_BYTE";
    return name.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(name[prefix.Length..], out int word)
        && word is >= 1 and <= 8 ? word - 1 : -1;
  }

  internal static bool IsReadOnly(string name) =>
      name is "MODEM_PROFILE" or "HW_VERSION" or "RADIO_COUNT"
      or "DIVERSITY"
      || IsNv4RefreshParameter(name)
      || name.EndsWith("_HASH", StringComparison.Ordinal)
      || name.EndsWith("_CHIP", StringComparison.Ordinal);

  internal static bool IsEditableValueAllowed(string name, double value) {
    if (name.EndsWith("_FRAME", StringComparison.Ordinal)) {
      return double.IsFinite(value)
          && value == Math.Truncate(value)
          && value is >= Nv5MinimumFrameBytes and <= Nv5MaximumFrameBytes
          && (long)value % Nv5FrameStepBytes == 0;
    }
    return name != "ENC_KEY_BITS" || value == 128;
  }

  internal static bool RequiresManualReboot(NvModemGeneration generation, string name) =>
      generation == NvModemGeneration.Nv5
      && !name.StartsWith("RTSP_", StringComparison.Ordinal);

  internal static string Group(string name) {
    if (Nv4KeyWordIndex(name) >= 0 || name == "ENC_KEY_BITS") {
      return "Radio encryption";
    }
    if (Nv4RadioParameters.Contains(name)) {
      return "RFM radio";
    }
    if (name.StartsWith("CH1_", StringComparison.Ordinal)) {
      return "Radio 1";
    }

    if (name.StartsWith("CH2_", StringComparison.Ordinal)) {
      return "Radio 2";
    }

    if (name.StartsWith("RTSP_", StringComparison.Ordinal)) {
      return "RTSP client";
    }

    if (name.StartsWith("LOCAL_IP", StringComparison.Ordinal)
        || name.StartsWith("NETMASK", StringComparison.Ordinal)
        || name.StartsWith("GATEWAY", StringComparison.Ordinal)
        || name.StartsWith("REMOTE_IP", StringComparison.Ordinal)
        || name.StartsWith("UDP_", StringComparison.Ordinal)
        || name.StartsWith("NET_", StringComparison.Ordinal)
        || name.StartsWith("PROXY_", StringComparison.Ordinal)
        || name is "ETH_ENABLE" or "UART_BAUD" or "SERIAL_BAUDRATE") {
      return "Network and transport";
    }
    if (name.StartsWith("AUX_", StringComparison.Ordinal)
        || name.StartsWith("SBUS_", StringComparison.Ordinal)
        || name.StartsWith("PWM", StringComparison.Ordinal)) {
      return "Auxiliary I/O";
    }
    return "System and MAVLink";
  }

  internal static string Description(string name) {
    string exact = name switch {
      "HW_VERSION" => "Read-only NV4 hardware generation. Version 3 selects the older primary "
          + "UART mapping; version 4 or later also enables the hardware mode button.",
      "WD_TIMEOUT" => "NV4 watchdog timeout in seconds. -1 calculates it from packet timing and "
          + "HOPS_WAITING, with a minimum of 10 seconds.",
      "DATA_REFLECT" => "Stored legacy diagnostic-reflection field. The current NV_MODEM_V4 "
          + "sketch does not read it, so changing it has no runtime effect.",
      "DATA_RF_STAT" => "Also emit each NV_RX_STAT message inside MAVLink ENCAPSULATED_DATA: "
          + "0=off, nonzero=on.",
      "RC_DELAY" => "Period in milliseconds for converting received SBUS data into "
          + "RC_CHANNELS_OVERRIDE messages on a transmitting modem.",
      "RX_RSSI_TYPE" => "Legacy RADIO_STATUS RSSI representation: 0=RSSI shifted by +164, "
          + "1=that shifted value scaled by 100/174.",
      "SBUS_ENABLE" => "NV4 serial routing: 0=SBUS disabled and both UARTs carry MAVLink, "
          + "1=SBUS on the primary UART, 2=SBUS on the secondary UART.",
      "SBUS_MASK" => "16-bit mask selecting which SBUS channels are copied into generated "
          + "RC_CHANNELS_OVERRIDE messages; bit 0 selects channel 1.",
      "LOCAL_SYS_ID" => "NV4 MAVLink system id. Changing addressing takes effect after settings are applied.",
      "LOCAL_COMP_ID" => "NV4 MAVLink component id and fourth octet of its local IPv4 address; "
          + "legacy provisioning normally uses 16..19.",
      "UAV_SYS_ID" => "Target MAVLink system id for generated RC_CHANNELS_OVERRIDE messages.",
      "UAV_COMP_ID" => "Target MAVLink component id for generated RC_CHANNELS_OVERRIDE messages.",
      "NET_PORT_LOCAL" => "Local UDP port receiving the primary telemetry stream.",
      "NET_PORT_REMOTE" => "Remote UDP destination port for the primary telemetry stream.",
      "PROXY_UDP_LPORT" => "Local UDP receive port for the NV4 proxy stream.",
      "PROXY_UDP_RPORT" => "Remote UDP destination port for the NV4 proxy stream.",
      "PROXY_RSSI" => "Stored legacy proxy-RSSI field. The current NV_MODEM_V4 sketch does not "
          + "read it, so changing it has no runtime effect.",
      "NET_ENABLE" => "NV4 Ethernet transport: 0=disabled, 1=enabled.",
      "TX_ON" => "NV4 radio role selected by bit 0: even values receive and odd values transmit; "
          + "normal values are 0 and 1. Mission Planner writes the firmware refresh trigger "
          + "automatically.",
      "SERIAL_BAUDRATE" => "Baud rate for UARTs carrying MAVLink: both UARTs when SBUS is off, "
          + "or the UART not assigned to SBUS in modes 1 and 2.",
      "UNITED_PKG_CNT" => "Number of 16-byte AES blocks combined into one fixed over-air packet; "
          + "peers must match.",
      "USE_FHSS" => "NV4 frequency hopping: 0=fixed frequency, 1=FHSS; peers must match.",
      "GUARD_INTERVAL" => "Fractional guard interval added to the calculated packet air time.",
      "CENTRAL_FREQ_MZ" => "Center RF frequency in MHz; peers and permitted regional band must match.",
      "BANDWIDTH_MHZ" => "Total NV4 FHSS frequency span in MHz around the center frequency.",
      "PREAMBLE_LEN" => "LoRa preamble length in symbols; 0 keeps the radio driver's default, "
          + "otherwise peers should use the same value.",
      "CHL_WIDE_KHZ" => "SX1278 LoRa channel bandwidth in kHz, normally 125, 250 or 500.",
      "ENC_KEY_BITS" => "NV4 legacy metadata; the firmware is compiled for AES-128 and does not use this field to select another key size. Keep it at 128.",
      "SPREAD_FACTOR" => "LoRa spreading factor; SX1278 normally supports 6..12.",
      "POWER_TX" => "SX1278 transmit power in dBm; applied only when TX_ON selects transmitter "
          + "mode and subject to hardware and regional limits.",
      "LNA_GAIN" => "SX1278 receive gain passed to the radio only in receiver mode: 0=automatic, "
          + "other supported values select fixed gain.",
      "CODING_RATE" => "LoRa coding-rate denominator: 5..8 represents 4/5 through 4/8.",
      "HOPS_WAITING" => "Unsynchronized receiver scan interval multiplier. Before the first valid "
          + "packet, frequency changes after this many packet intervals; it also scales automatic "
          + "WD_TIMEOUT.",
      "SYNC_WORD" => "LoRa sync word byte, 0..255; peers must match.",
      "HARDWARE_CRC" => "SX1278 packet CRC: 0=disabled, 1=enabled; peers must match.",
      "NET_BYTE_REMOTE" => "Fourth octet of the remote IPv4 address; its first three octets come "
          + "from NET_BYTE_1..3.",
      "CHECK_SYNC_WORD" => "SX1278 bit synchronization: 0=disable it, nonzero=enable it and apply "
          + "SYNC_WORD.",
      "ACCEPT_UNKN_MAV" => "Forward MAVLink messages with unknown ids: 0=reject, 1=accept.",
      "UART2_STAT_ON" => "Allow non-forced locally generated MAVLink, including radio statistics, "
          + "on the secondary UART: 0=off, nonzero=on.",
      "DEV_MODE" => "Legacy development mode: enables the hardware mode button and forwards "
          + "USB-injected MAVLink into the RF transmit queue. Leave disabled in normal operation.",
      "REFRESH_SETTING" or "REFRESH_SETTINGS" => "Internal NV4 apply trigger. Old firmware names "
          + "it REFRESH_SETTINGS; newer builds use REFRESH_SETTING. Mission Planner writes the "
          + "advertised name automatically.",
      "MODEM_PROFILE" => "Read-only provisioned product profile identifier.",
      "RADIO_COUNT" => "Read-only number of detected and enabled radio chips.",
      "RTSP_ENABLE" => "RTSP client: 0=disabled, 1=connect automatically using the settings below.",
      "RTSP_OUTPUT" => "RTSP depacketizer output: 0=Annex-B elementary stream, 1=raw RTP.",
      "RTSP_PORT" => "RTSP TCP control port, 1..65535.",
      "RTSP_RTP_PORT" => "Even local RTP UDP port, 1024..65534.",
      "AUX_UART_MODE" => "Auxiliary UART: 0=disabled, 1=MAVLink, 2=SBUS receive, 3=SBUS transmit.",
      "PWM_SRC_PORT" => "SERVO_OUTPUT_RAW source: 0=UART, 1=UDP, 255=any local management port.",
      "MAV_SYS_ID" => "MAVLink system id, 1..255; used to address this modem.",
      "MAV_SAVE_MS" => "Debounce before saving PARAM changes to flash, 100..65535 ms.",
      "UART_BAUD" => "Primary application/management UART baud rate.",
      "UDP_RX_BASE" => "Local UDP receive port for FLRC video stream 0. "
          + "This parameter is absent when no LR2021 FLRC channel is active.",
      "UDP_TX_BASE" => "Remote UDP destination port for FLRC video stream 0. "
          + "This parameter is absent when no LR2021 FLRC channel is active.",
      "MAV_LPORT" => "Local UDP port on which the modem receives MAVLink management traffic.",
      "MAV_RPORT" => "Remote UDP destination port for MAVLink status and replies.",
      "ETH_ENABLE" => "Ethernet interface: 0=disabled, 1=enabled.",
      "DIVERSITY" => "Read-only derived topology flag: two active radios with equal RX or TX roles "
          + "always use diversity. Frequencies, FHSS and encryption keys remain independent; "
          + "packet boundaries must match.",
      "MAV_ENABLE" or "SBUS_EXT_INV" =>
          "Boolean setting: 0=disabled, 1=enabled.",
      _ => "",
    };
    if (exact.Length != 0) {
      return exact;
    }
    if (Nv4KeyWordIndex(name) >= 0) {
      return "NV4 raw key word (MAVLink INT32), range -2147483648..2147483647. "
          + "Each word stores four bytes in little-endian order. Words 1..4 supply the AES-128 "
          + "key; all eight words also affect the FHSS schedule, so peers must match all eight.";
    }
    if (name.StartsWith("NET_BYTE_", StringComparison.Ordinal)) {
      return "One of the first three IPv4 octets shared by the local and remote addresses. "
          + "LOCAL_COMP_ID supplies the local fourth octet.";
    }

    if (name.StartsWith("NET_MASK_", StringComparison.Ordinal)) {
      return "One IPv4 subnet-mask octet, 0..255.";
    }

    if (name.EndsWith("_CHIP", StringComparison.Ordinal)) {
      return "Read-only: 0=LR2021, 1=LR1110, 2=LR1120, 3=LR1121, 4=SX1276, 5=SX1278.";
    }

    if (name.EndsWith("_ROLE", StringComparison.Ordinal)) {
      return "0=receiver, 1=transmitter, 2=transceiver; must match the peer topology.";
    }

    if (name.EndsWith("_MOD", StringComparison.Ordinal)) {
      return "0=LoRa, 1=FLRC; changing this replaces the modulation-specific parameter list.";
    }

    if (name.EndsWith("_FRAME", StringComparison.Ordinal)) {
      return "Fixed on-air frame size in bytes: 64..240 in steps of 16, or up to 496 for "
          + "LR2021 FLRC; peers must use the same value.";
    }

    if (name.EndsWith("_FREQ_KHZ", StringComparison.Ordinal)) {
      return "Center RF frequency in kHz (for example, 868000 = 868 MHz); the complete FHSS span must fit the radio band.";
    }

    if (name.EndsWith("_PWR_DBM", StringComparison.Ordinal)) {
      return "Radio output power in dBm; valid limits depend on the detected chip and band.";
    }

    if (name.EndsWith("_FHSS", StringComparison.Ordinal)) {
      return "0=fixed frequency, 1=FHSS; peers must use the same setting.";
    }

    if (name.EndsWith("_FHSS_KHZ", StringComparison.Ordinal)) {
      return "Total FHSS span in kHz around the center frequency.";
    }

    if (name.EndsWith("_GUARD_US", StringComparison.Ordinal)) {
      return "Fixed hop guard interval in microseconds; setting it clears adaptive guard modes.";
    }

    if (name.EndsWith("_DWELL_SH", StringComparison.Ordinal)) {
      return "FHSS dwell exponent 0..23: one frequency is held for 2^value packets.";
    }

    if (name.EndsWith("_SYNC_PER", StringComparison.Ordinal)) {
      return "FHSS synchronization period in packet groups, 1..65535.";
    }

    if (name.EndsWith("_SCAN_DW", StringComparison.Ordinal)) {
      return "Receiver scan dwell in packet groups, 1..65535.";
    }

    if (name.EndsWith("_ENCRYPT", StringComparison.Ordinal)) {
      return "AES-128 payload encryption: 0=disabled, 1=enabled; key bytes must match the peer.";
    }

    if (name.EndsWith("_KEY_HASH", StringComparison.Ordinal)) {
      return "Read-only unsigned fingerprint of the stored key; compare peers without exposing the key.";
    }

    if (name.EndsWith("_LINK_HASH", StringComparison.Ordinal)) {
      return "Read-only unsigned 32-bit fingerprint of the complete local over-air profile.";
    }

    if (name.EndsWith("_PEER_HASH", StringComparison.Ordinal)) {
      return "Read-only stored unsigned 32-bit peer-profile fingerprint; 0 means the link was not pair-provisioned.";
    }

    if (Nv5KeyWordIndex(name) >= 0) {
      return "AES-128 key word (MAVLink INT32), range -2147483648..2147483647. "
          + "W0 contains bytes 0..3, W1 bytes 4..7, W2 bytes 8..11, and W3 bytes 12..15, "
          + "each in big-endian order. The signed decimal value preserves the same four raw "
          + "bytes; configure all four words identically on linked radios.";
    }

    if (name.EndsWith("_RADIO_CRC", StringComparison.Ordinal)) {
      return "Radio hardware CRC: 0=disabled, 1=enabled; independent of the NV5 frame CRC.";
    }

    if (name.EndsWith("_TCXO_MV", StringComparison.Ordinal)) {
      return "TCXO supply voltage in millivolts; 0 selects the board/default behavior.";
    }

    if (name.EndsWith("_DIRECT_FREQ", StringComparison.Ordinal)) {
      return "LR-family laboratory option: 1 bypasses normal image-calibration gating.";
    }

    if (name.EndsWith("_RX_BOOST", StringComparison.Ordinal)) {
      return "LR2021 RX path level 0..7; 8 selects automatic mode.";
    }

    if (name.EndsWith("_LNA_GAIN", StringComparison.Ordinal)) {
      return "Receive LNA gain: 0=AGC; LR2021 supports 1..13 and SX127x supports 1..6.";
    }

    if (name.EndsWith("_GUARD_MULT", StringComparison.Ordinal)) {
      return "Adaptive FHSS guard multiplier: 0=off or 0.000001..10; clears other guard modes.";
    }

    if (name.EndsWith("_OPEN_LOOP", StringComparison.Ordinal)) {
      return "Open-loop FHSS receive: 0=off, 1=on; valid only for an FHSS receiver.";
    }

    if (name.EndsWith("_LINK_MS", StringComparison.Ordinal)) {
      return "Loss-of-link timeout in milliseconds.";
    }

    if (name.EndsWith("_OWNS_SCHED", StringComparison.Ordinal)) {
      return "Transceiver schedule: 1=local timing owner, 0=follower.";
    }

    if (name.EndsWith("_TX_PERIOD", StringComparison.Ordinal)) {
      return "Transceiver transmit period in microseconds; must be greater than zero.";
    }

    if (name.EndsWith("_TX_PHASE", StringComparison.Ordinal)) {
      return "Transceiver transmit phase in microseconds; must be below TX period.";
    }

    if (name.EndsWith("_LORA_KHZ", StringComparison.Ordinal)) {
      return "LoRa bandwidth in kHz: normally 125, 250 or 500; fractional kHz values are supported.";
    }

    if (name.EndsWith("_LORA_SF", StringComparison.Ordinal)) {
      return "LoRa spreading factor 5..12 (SX127x supports 6..12).";
    }

    if (name.EndsWith("_LORA_CR", StringComparison.Ordinal)) {
      return "LoRa coding rate denominator 5..8, representing 4/5 through 4/8.";
    }

    if (name.EndsWith("_LORA_SYNC", StringComparison.Ordinal)) {
      return "LoRa sync word byte, decimal 0..255; must match the peer.";
    }

    if (name.EndsWith("_LORA_PRE", StringComparison.Ordinal)) {
      return "LoRa preamble length in symbols, 1..65535.";
    }

    if (name.EndsWith("_FEC", StringComparison.Ordinal)) {
      return "Outer erasure FEC for FLRC: 0=disabled, 1=enabled.";
    }

    if (name.EndsWith("_FEC_K", StringComparison.Ordinal)) {
      return "FEC source packets K, 1..16; K must not exceed N.";
    }

    if (name.EndsWith("_FEC_N", StringComparison.Ordinal)) {
      return "FEC total packets N, 1..16; N must not be below K.";
    }

    if (name.EndsWith("_FLRC_RATE", StringComparison.Ordinal)) {
      return "FLRC bitrate in bit/s: 260k, 325k, 520k, 650k, 1.04M, 1.3M, 2.08M or 2.6M.";
    }

    if (name.EndsWith("_FLRC_CR", StringComparison.Ordinal)) {
      return "FLRC coding rate: 0=1/2, 1=3/4, 2=1/1, 3=2/3.";
    }

    if (name.EndsWith("_FLRC_SHAPE", StringComparison.Ordinal)) {
      return "FLRC Gaussian shaping: 0=none, 1=BT0.3, 2=BT0.5, 3=BT0.7, 4=BT1.0.";
    }

    if (name.EndsWith("_FLRC_PRE", StringComparison.Ordinal)) {
      return "FLRC preamble length: 4..64 bits in steps of four.";
    }

    if (name.Contains("_FLRC_SYNC", StringComparison.Ordinal)) {
      return "One byte of the four-byte FLRC sync word, decimal 0..255; must match the peer.";
    }

    if (name.EndsWith("_CH_NUMBER", StringComparison.Ordinal)) {
      return "SERVO_OUTPUT_RAW channel: -1=disabled, 1..16=servo number.";
    }

    if (name.Contains("IP_", StringComparison.Ordinal)
        || name.StartsWith("NETMASK_", StringComparison.Ordinal)) {
      return "One IPv4 octet, 0..255; all four octets are saved together.";
    }

    if (name.EndsWith("_MS", StringComparison.Ordinal)) {
      return "Milliseconds.";
    }

    if (name.EndsWith("_US", StringComparison.Ordinal)) {
      return "Microseconds.";
    }

    if (name.Contains("PORT", StringComparison.Ordinal)) {
      return "UDP/TCP port, 1..65535.";
    }

    return "Published by the modem through the standard MAVLink parameter protocol.";
  }

  internal static bool Applicable(string name, IReadOnlyDictionary<string, double> staged) {
    bool lora = name.StartsWith("CH1_LORA_", StringComparison.Ordinal)
        || name.StartsWith("CH2_LORA_", StringComparison.Ordinal);
    bool flrc = name.StartsWith("CH1_FLRC_", StringComparison.Ordinal)
        || name.StartsWith("CH2_FLRC_", StringComparison.Ordinal)
        || name is "CH1_FEC" or "CH1_FEC_K" or "CH1_FEC_N"
            or "CH2_FEC" or "CH2_FEC_K" or "CH2_FEC_N";
    if (!lora && !flrc) {
      return !IsNv4RefreshParameter(name);
    }
    string modeName = name.StartsWith("CH1_", StringComparison.Ordinal)
        ? "CH1_MOD" : "CH2_MOD";
    if (!staged.TryGetValue(modeName, out double value)) {
      return true;
    }
    long mode = (long)Math.Round(value);
    return mode is not (0 or 1) || (lora ? mode == 0 : mode == 1);
  }

  internal static string HardwareModel(
      NvModemGeneration generation, uint profile, IEnumerable<byte>? chips = null,
      bool hasModemInfo = false) {
    if (generation == NvModemGeneration.Nv4 && !hasModemInfo) {
      return "Teensy · RFM/SX1278";
    }
    string model = profile switch {
      1 => "STM32.V5 direct-stack (legacy)",
      2 => "STM32.V5 2RX EXTI9 (legacy)",
      3 => "STM32.V5 2RX Radio-A (legacy)",
      4 => "Teensy.V4 RFM-433",
      5 => "Teensy.V4 RFM-868",
      6 => "Teensy.V5 2RX split-SPI",
      7 => "STM32.V5",
      8 => "Teensy.V5 2RX shared-SPI",
      _ => generation == NvModemGeneration.Nv5 ? "NV5 hardware unknown" : "hardware unknown",
    };
    if ((profile is 0 or 7 || generation == NvModemGeneration.Nv4) && chips != null) {
      string[] names = [.. chips.Distinct().Select(ChipName)];
      if (names.Length != 0) {
        model += " · " + string.Join(" + ", names);
      }
    }
    return model;
  }

  internal static string ChipName(byte chip) => chip switch {
    0 => "LR2021",
    1 => "LR1110",
    2 => "LR1120",
    3 => "LR1121",
    4 => "SX1276",
    5 => "SX1278",
    _ => chip.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

  internal static string RoleName(byte role) => role switch {
    0 => "RX",
    1 => "TX",
    2 => "TRX",
    _ => role.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

  internal static string ModulationName(byte modulation) => modulation switch {
    0 => "LoRa",
    1 => "FLRC",
    _ => modulation.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

  private static readonly HashSet<string> Nv4RadioParameters = new(StringComparer.Ordinal) {
    "TX_ON", "USE_FHSS", "GUARD_INTERVAL", "CENTRAL_FREQ_MZ", "BANDWIDTH_MHZ",
    "PREAMBLE_LEN", "CHL_WIDE_KHZ", "SPREAD_FACTOR", "POWER_TX", "LNA_GAIN",
    "CODING_RATE", "HOPS_WAITING", "SYNC_WORD", "HARDWARE_CRC", "CHECK_SYNC_WORD",
    "UNITED_PKG_CNT",
  };
}
