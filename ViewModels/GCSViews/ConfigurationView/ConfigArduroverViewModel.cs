namespace MissionPlanner.ViewModels.GCSViews.ConfigurationView;

public class ConfigArduroverViewModel : TuningPageBase {
  public ConfigArduroverViewModel() {
    Title = "ArduRover Pids";
    Intro = "Rover basic tuning. Fields show n/a if not present on this firmware.";
    Rebuild();
  }

  protected override void Build() {
    Groups.Add(new TuningGroup("Throttle and Motors")
        .Combo("Motor Type", "MOT_PWM_TYPE")
        .Num("Throttle Max (%)", "MOT_THR_MAX", "THR_MAX")
        .Num("Throttle Min (%)", "MOT_THR_MIN", "THR_MIN"));

    Groups.Add(new TuningGroup("Speed/Throttle")
        .Num("Cruise Speed", "CRUISE_SPEED")
        .Num("Cruise Throttle", "CRUISE_THROTTLE")
        .Combo("Brake", "ATC_BRAKE")
        .Num("Accel Max (m/s/s)", "ATC_ACCEL_MAX")
        .Num("Decel Max (m/s/s)", "ATC_DECEL_MAX")
        .Num("P", "SPEED2THR_P", "ATC_SPEED_P")
        .Num("I", "SPEED2THR_I", "ATC_SPEED_I")
        .Num("D", "SPEED2THR_D", "ATC_SPEED_D")
        .Num("IMAX", "SPEED2THR_IMAX", "ATC_SPEED_IMAX"));

    Groups.Add(new TuningGroup("Navigation")
        .Num("WP Speed", "WP_SPEED")
        .Num("WP Radius", "WP_RADIUS")
        .Num("Turn Radius", "TURN_RADIUS")
        .Num("Turn G Max", "TURN_MAX_G", "ATC_TURN_MAX_G")
        .Num("Pivot Angle", "WP_PIVOT_ANGLE")
        .Num("Pivot Rate", "WP_PIVOT_RATE"));

    Groups.Add(new TuningGroup("Position Controller")
        .Num("Position P", "PSC_POS_P")
        .Num("Velocity P", "PSC_VEL_P")
        .Num("Velocity I", "PSC_VEL_I")
        .Num("Velocity D", "PSC_VEL_D")
        .Num("Velocity IMAX", "PSC_VEL_IMAX")
        .Num("Velocity FF", "PSC_VEL_FF"));

    Groups.Add(new TuningGroup("Steering Angle")
        .Num("P", "ATC_STR_ANG_P")
        .Num("Acceleration Max", "ATC_STR_ACC_MAX"));

    Groups.Add(new TuningGroup("Steering Rate")
        .Num("P", "STEER2SRV_P", "ATC_STR_RAT_P")
        .Num("I", "STEER2SRV_I", "ATC_STR_RAT_I")
        .Num("D", "STEER2SRV_D", "ATC_STR_RAT_D")
        .Num("IMAX", "STEER2SRV_IMAX", "ATC_STR_RAT_IMAX")
        .Num("FF", "ATC_STR_RAT_FF"));

    var avoidance = new TuningGroup("Avoidance") {
      IsVisible = comPort.MAV.param.ContainsKey("SONAR_TRIGGER_CM")
                  || comPort.MAV.param.ContainsKey("RNGFND_TRIGGR_CM"),
    };
    avoidance
        .Num("Trigger Dist (cm)", "SONAR_TRIGGER_CM", "RNGFND_TRIGGR_CM")
        .Num("Turn Angle", "SONAR_TURN_ANGLE", "RNGFND_TURN_ANGL")
        .Num("Turn Time", "SONAR_TURN_TIME", "RNGFND_TURN_TIME")
        .Num("Sonar Debounce", "SONAR_DEBOUNCE", "RNGFND_DEBOUNCE");
    Groups.Add(avoidance);

    Groups.Add(new TuningGroup("Channel Options")
        .Combo("RC7 Opt", "CH7_OPTION", "RC7_OPTION")
        .Combo("RC8 Opt", "CH8_OPTION", "RC8_OPTION")
        .Combo("RC9 Opt", "CH9_OPTION", "RC9_OPTION")
        .Combo("RC10 Opt", "CH10_OPTION", "RC10_OPTION"));
  }
}
