using MissionPlanner.ViewModels.GCSViews.ConfigurationView;

namespace MissionPlanner.Tests;

public class ParamFieldTests {
  [Fact]
  public void Unknown_param_offline_is_not_present_and_numeric_by_default() {
    var f = new ParamField("ZZ_DOES_NOT_EXIST");
    Assert.False(f.Exists);
    Assert.True(f.IsNumeric);
    Assert.False(f.IsBool);
    Assert.False(f.IsCombo);
  }

  [Fact]
  public void Kind_bool_sets_bool_field() {
    var f = new ParamField("ZZ_BOOL", "bool");
    Assert.True(f.IsBool);
    Assert.False(f.IsNumeric);
  }

  [Fact]
  public void Kind_combo_sets_combo_field() {
    var f = new ParamField("ZZ_COMBO", "combo");
    Assert.True(f.IsCombo);
    Assert.False(f.IsNumeric);
  }

  [Fact]
  public void Bitmask_editor_does_not_round_bits_above_float_integer_precision() {
    var field = new ParamField("ZZ_BITMASK", "bitmask");
    var bit24 = new BitOption(field, 24, "High bit");
    var bit2 = new BitOption(field, 2, "Low bit");
    field.BitOptions.Add(bit24);
    field.BitOptions.Add(bit2);

    bit24.IsSet = true;
    bit2.IsSet = true;

    Assert.Equal(16_777_220d, field.Value);
  }
}
