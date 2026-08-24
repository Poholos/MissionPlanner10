using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using MissionPlanner.ArduPilot;

namespace MissionPlanner.Tests;

public sealed class MissionUploadProtocolTests {
  [Theory]
  [InlineData(nameof(mav_mission.upload))]
  [InlineData(nameof(mav_mission.uploadPartial))]
  public void Upload_does_not_acknowledge_the_vehicle_mission_ack(string methodName) {
    MethodInfo method = typeof(mav_mission).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(candidate => candidate.Name == methodName);
    Type stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()!
        .StateMachineType;
    MethodInfo moveNext = stateMachine.GetMethod(
        "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;

    Assert.DoesNotContain(CalledMethods(moveNext), called =>
        called.DeclaringType == typeof(MAVLinkInterface)
        && called.Name == nameof(MAVLinkInterface.setWPACK));
  }

  private static IEnumerable<MethodBase> CalledMethods(MethodInfo method) {
    byte[] bytes = method.GetMethodBody()!.GetILAsByteArray()!;
    Module module = method.Module;
    Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? [];
    Type[] methodArguments = method.GetGenericArguments();
    int offset = 0;
    while (offset < bytes.Length) {
      OpCode opcode = ReadOpcode(bytes, ref offset);
      if (opcode.OperandType is OperandType.InlineMethod) {
        int token = BitConverter.ToInt32(bytes, offset);
        MethodBase? called = null;
        try {
          called = module.ResolveMethod(token, typeArguments, methodArguments);
        } catch (ArgumentException) {
        }
        if (called != null) {
          yield return called;
        }
      }
      offset += OperandSize(opcode.OperandType, bytes, offset);
    }
  }

  private static OpCode ReadOpcode(byte[] bytes, ref int offset) {
    ushort value = bytes[offset++];
    if (value == 0xfe) {
      value = (ushort)(0xfe00 | bytes[offset++]);
    }
    return Opcodes[value];
  }

  private static int OperandSize(OperandType type, byte[] bytes, int offset) => type switch {
    OperandType.InlineNone => 0,
    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
        or OperandType.ShortInlineVar => 1,
    OperandType.InlineVar => 2,
    OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField
        or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
        or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
    OperandType.InlineI8 or OperandType.InlineR => 8,
    OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, offset) * 4,
    _ => throw new InvalidOperationException("Unsupported IL operand: " + type),
  };

  private static readonly IReadOnlyDictionary<ushort, OpCode> Opcodes =
      typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
          .Where(field => field.FieldType == typeof(OpCode))
          .Select(field => (OpCode)field.GetValue(null)!)
          .ToDictionary(opcode => unchecked((ushort)opcode.Value));
}
