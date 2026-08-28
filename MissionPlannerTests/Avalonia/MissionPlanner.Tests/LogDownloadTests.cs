using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

public class LogDownloadTests {
  [Fact]
  public void Untimed_log_uses_a_stable_id_filename() {
    Assert.Equal("log_42.bin", LogDownloadViewModel.SuggestedFileName(
        new LogDownloadRow { Id = 42, TimeUtc = DateTime.MinValue }));
  }

  [Fact]
  public void Timed_log_filename_is_cross_platform_and_collision_resistant() {
    string name = LogDownloadViewModel.SuggestedFileName(new LogDownloadRow {
      Id = 7,
      TimeUtc = new DateTime(2026, 8, 21, 10, 11, 12, DateTimeKind.Local),
    });

    Assert.Equal("2026-08-21 10-11-12_7.bin", name);
    Assert.DoesNotContain(':', name);
  }

  [Fact]
  public void Tracker_ignores_a_short_packet_below_the_highest_offset_seen() {
    var tracker = new LogDownloadTracker();

    Assert.True(tracker.Add(0, 90, true));
    Assert.True(tracker.Add(90, 90, true));

    // a stale short retransmit of an earlier block must not set the total
    Assert.True(tracker.Add(0, 40, true));
    Assert.Null(tracker.TotalLength);

    // the true end, at the highest offset seen, still does
    Assert.True(tracker.Add(180, 30, true));
    Assert.Equal(210u, tracker.TotalLength);
  }

  [Fact]
  public void Tracker_counts_out_of_order_and_duplicate_data_only_once() {
    var tracker = new LogDownloadTracker();

    Assert.True(tracker.Add(90, 90, true));
    Assert.True(tracker.Add(0, 90, true));
    Assert.True(tracker.Add(90, 90, true));

    Assert.Equal(180UL, tracker.CoveredBytes);
    Assert.Null(tracker.TotalLength);
    LogDownloadRequest next = tracker.NextRequest(4500);
    Assert.Equal(180U, next.Offset);
    Assert.Equal(uint.MaxValue, next.Count);
  }

  [Fact]
  public void Tracker_requests_only_the_first_missing_range_after_finding_end() {
    var tracker = new LogDownloadTracker();
    tracker.Add(0, 90, true);
    tracker.Add(180, 20, true);

    Assert.Equal(200U, tracker.TotalLength);
    Assert.Equal(110UL, tracker.CoveredBytes);
    Assert.False(tracker.IsComplete);

    LogDownloadRequest missing = tracker.NextRequest(4500);
    Assert.Equal(90U, missing.Offset);
    Assert.Equal(90U, missing.Count);

    tracker.Add(90, 90, false);
    Assert.True(tracker.IsComplete);
    Assert.Equal(200UL, tracker.CoveredBytes);
  }

  [Fact]
  public void Tracker_accepts_zero_length_terminator_for_packet_aligned_log() {
    var tracker = new LogDownloadTracker();
    tracker.Add(0, 90, true);
    tracker.Add(90, 90, true);
    tracker.Add(180, 0, true);

    Assert.Equal(180U, tracker.TotalLength);
    Assert.True(tracker.IsComplete);
    Assert.Equal(180UL, tracker.CoveredBytes);
  }

  [Fact]
  public void Tracker_merges_partially_overlapping_ranges() {
    var tracker = new LogDownloadTracker();
    tracker.Add(30, 90, true);
    tracker.Add(0, 90, true);

    Assert.Equal(120UL, tracker.CoveredBytes);
    Assert.Equal(120U, tracker.NextRequest(4500).Offset);
  }

  [Fact]
  public void Tracker_rejects_a_block_that_overflows_the_protocol_offset() {
    var tracker = new LogDownloadTracker();

    Assert.False(tracker.Add(uint.MaxValue - 10, 90, true));
    Assert.Equal(0UL, tracker.CoveredBytes);
    Assert.Null(tracker.TotalLength);
  }
}
