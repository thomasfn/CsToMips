using System.Reflection;

namespace CsToMips.Compiler
{
    internal abstract class StackValue
    {
        public abstract string AsIC10 { get; }
    }

    internal class StaticStackValue : StackValue
    {
        public float Value { get; set; }

        public override string AsIC10 { get => $"{Value}"; }
    }

    internal class ThisStackValue : StackValue
    {
        public override string AsIC10 { get => throw new InvalidOperationException(); }
    }

    internal class DeviceStackValue : StackValue
    {
        public string PinName { get; set; } = "";
        public Type DeviceType { get; set; } = null!;
        public bool Multicast { get; set; }

        public override string AsIC10 { get => PinName; }
    }

    internal class RegisterStackValue : StackValue
    {
        public int RegisterIndex { get; set; }

        public override string AsIC10 { get => $"r{RegisterIndex}"; }
    }

    internal class FieldStackValue : StackValue
    {
        public string AliasName { get; set; } = "";

        public FieldInfo UnderlyingField { get; set; } = null!;

        public override string AsIC10 { get => AliasName; }
    }
}
