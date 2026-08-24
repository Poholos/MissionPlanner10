namespace MissionPlanner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentStateUnitsCollection {
  public const string Name = "CurrentState unit settings";
}

[Collection(CurrentStateUnitsCollection.Name)]
public sealed class CurrentStateDistanceTests {
  [Fact]
  public void Travelled_distance_changes_display_units_without_changing_physical_distance() {
    float previousMultiplier = CurrentState.multiplierdist;
    string previousUnit = CurrentState.DistanceUnit;
    try {
      CurrentState.multiplierdist = 1;
      CurrentState.DistanceUnit = "m";
      var state = new CurrentState { distTraveled = 100 };
      state.battery_usedmah = 50;

      Assert.Equal(100, state.distTraveled, 4);
      Assert.Equal(500, state.battery_mahperkm, 4);

      CurrentState.multiplierdist = 3.2808399f;
      CurrentState.DistanceUnit = "ft";

      Assert.Equal(328.08399, state.distTraveled, 3);
      Assert.Equal(500, state.battery_mahperkm, 4);
    } finally {
      CurrentState.multiplierdist = previousMultiplier;
      CurrentState.DistanceUnit = previousUnit;
    }
  }

  [Fact]
  public void Travelled_distance_setter_interprets_the_current_display_unit() {
    float previousMultiplier = CurrentState.multiplierdist;
    string previousUnit = CurrentState.DistanceUnit;
    try {
      CurrentState.multiplierdist = 3.2808399f;
      CurrentState.DistanceUnit = "ft";
      var state = new CurrentState { distTraveled = 328.08399f };

      CurrentState.multiplierdist = 1;
      CurrentState.DistanceUnit = "m";

      Assert.Equal(100, state.distTraveled, 3);
    } finally {
      CurrentState.multiplierdist = previousMultiplier;
      CurrentState.DistanceUnit = previousUnit;
    }
  }
}
