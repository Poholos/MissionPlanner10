using Avalonia.Headless.XUnit;
using MissionPlanner.Utilities;
using MissionPlanner.ViewModels;

namespace MissionPlanner.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FlightPlannerHomeSyncCollection {
  public const string Name = "Flight Planner home synchronization";
}

[Collection(FlightPlannerHomeSyncCollection.Name)]
public class FlightPlannerHomeSyncTests {
  [Fact]
  public void Reported_home_replaces_saved_home() {
    using var scope = new PlannerScope();
    FlightPlannerViewModel vm = scope.ViewModel;
    SetSanFranciscoHome(vm);

    vm.RefreshHomeFromVehicle(
        new PointLatLngAlt(50.344417763, 30.89499303, 132),
        new PointLatLngAlt(-35.363261, 149.165230, 584));

    Assert.Equal(50.344417763, vm.HomeLat, 8);
    Assert.Equal(30.89499303, vm.HomeLng, 8);
    Assert.Equal(132, vm.HomeAlt, 6);
  }

  [Fact]
  public void Planner_home_refresh_falls_back_to_planned_home() {
    using var scope = new PlannerScope();
    FlightPlannerViewModel vm = scope.ViewModel;

    vm.RefreshHomeFromVehicle(
        new PointLatLngAlt(),
        new PointLatLngAlt(50.344417763, 30.89499303, 132));

    Assert.Equal(50.344417763, vm.HomeLat, 8);
    Assert.Equal(30.89499303, vm.HomeLng, 8);
    Assert.Equal(132, vm.HomeAlt, 6);
  }

  [Fact]
  public void Planner_home_refresh_keeps_saved_home_without_vehicle_coordinates() {
    using var scope = new PlannerScope();
    FlightPlannerViewModel vm = scope.ViewModel;
    SetSanFranciscoHome(vm);

    vm.RefreshHomeFromVehicle(new PointLatLngAlt(), new PointLatLngAlt());

    Assert.Equal(37.619373, vm.HomeLat, 8);
    Assert.Equal(-122.376637, vm.HomeLng, 8);
    Assert.Equal(5.28, vm.HomeAlt, 6);
  }

  [AvaloniaFact]
  public async Task Navigating_to_plan_refreshes_stale_home_from_vehicle() {
    CurrentState state = AppState.comPort.MAV.cs;
    PointLatLngAlt previousReportedHome = state.HomeLocation;
    PointLatLngAlt previousPlannedHome = state.PlannedHomeLocation;
    var shell = new MainWindowViewModel();
    var savedPlannerHome = (
        shell.FlightPlanner.HomeLat,
        shell.FlightPlanner.HomeLng,
        shell.FlightPlanner.HomeAlt);
    try {
      SetSanFranciscoHome(shell.FlightPlanner);
      state.HomeLocation = new PointLatLngAlt(50.344417763, 30.89499303, 132);
      state.PlannedHomeLocation = new PointLatLngAlt(-35.363261, 149.165230, 584);

      await shell.NavigateCommand.ExecuteAsync("PLAN");

      Assert.IsType<FlightPlannerViewModel>(shell.CurrentScreen);
      Assert.Equal(50.344417763, shell.FlightPlanner.HomeLat, 8);
      Assert.Equal(30.89499303, shell.FlightPlanner.HomeLng, 8);
      Assert.Equal(132, shell.FlightPlanner.HomeAlt, 6);

      state.HomeLocation = new PointLatLngAlt(-35.363261, 149.165230, 584);

      await shell.NavigateCommand.ExecuteAsync("PLAN");

      Assert.Equal(-35.363261, shell.FlightPlanner.HomeLat, 8);
      Assert.Equal(149.165230, shell.FlightPlanner.HomeLng, 8);
      Assert.Equal(584, shell.FlightPlanner.HomeAlt, 6);
    } finally {
      state.HomeLocation = previousReportedHome;
      state.PlannedHomeLocation = previousPlannedHome;
      shell.FlightPlanner.HomeLat = savedPlannerHome.HomeLat;
      shell.FlightPlanner.HomeLng = savedPlannerHome.HomeLng;
      shell.FlightPlanner.HomeAlt = savedPlannerHome.HomeAlt;
      shell.Dispose();
    }
  }

  private static void SetSanFranciscoHome(FlightPlannerViewModel vm) {
    vm.HomeLat = 37.619373;
    vm.HomeLng = -122.376637;
    vm.HomeAlt = 5.28;
  }

  private sealed class PlannerScope : IDisposable {
    private readonly (double Lat, double Lng, double Alt) _savedHome;

    public PlannerScope() {
      ViewModel = new FlightPlannerViewModel();
      _savedHome = (ViewModel.HomeLat, ViewModel.HomeLng, ViewModel.HomeAlt);
    }

    public FlightPlannerViewModel ViewModel { get; }

    public void Dispose() {
      ViewModel.HomeLat = _savedHome.Lat;
      ViewModel.HomeLng = _savedHome.Lng;
      ViewModel.HomeAlt = _savedHome.Alt;
      ViewModel.Dispose();
    }
  }
}
