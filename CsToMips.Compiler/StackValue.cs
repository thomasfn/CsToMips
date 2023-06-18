using System.Collections.Immutable;
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

    internal class StringStackValue : StackValue
    {
        public string Value { get; set; } = "";

        public override string AsIC10 { get => Value; }
    }

    internal class HashStringStackValue : StackValue
    {
        public string Value { get; set; } = "";

        public override string AsIC10 { get => $"HASH(\"{Value}\")"; }
    }

    internal class NullStackValue : StackValue
    {
        public override string AsIC10 { get { throw new InvalidOperationException(); } }
    }

    internal class DeviceSlotsStackValue : StackValue
    {
        public string PinName { get; set; } = "";

        public Type DeviceType { get; set; } = null!;

        public override string AsIC10 { get { throw new InvalidOperationException(); } }
    }

    internal class DeviceSlotStackValue : StackValue
    {
        public string PinName { get; set; } = "";

        public Type DeviceType { get; set; } = null!;

        public StackValue SlotIndex { get; set; } = null!;

        public override string AsIC10 { get { throw new InvalidOperationException(); } }
    }

    internal class DeferredExpressionStackValue : StackValue
    {
        public FragmentText ExpressionText { get; set; }

        public ImmutableArray<StackValue> FreeValues { get; set; }

        public override string AsIC10 { get { throw new InvalidOperationException(); } }
    }

    internal readonly struct VirtualStack : IEquatable<VirtualStack>
    {
        public static readonly VirtualStack Empty = new(ReadOnlySpan<StackValue>.Empty);

        private readonly StackValue[] stack;

        public ReadOnlySpan<StackValue> Stack => stack;

        public int Depth => Stack.Length;

        public VirtualStack(ReadOnlySpan<StackValue> stack)
        {
            this.stack = stack.ToArray();
        }

        public static VirtualStack FromEnumerable(IEnumerable<StackValue> enumerable)
            => new(enumerable.ToArray());

        public override bool Equals(object? obj) => obj is VirtualStack value && Equals(value);

        public bool Equals(VirtualStack other) => Stack.SequenceEqual(other.Stack);

        public override int GetHashCode()
        {
            int hash = (0).GetHashCode();
            foreach (var item in stack)
            {
                hash = HashCode.Combine(hash, item);
            }
            return hash;
        }

        public static bool operator ==(VirtualStack? left, VirtualStack? right) => left.Equals(right);

        public static bool operator !=(VirtualStack? left, VirtualStack? right) => !(left == right);

        public VirtualStack Push(StackValue stackValue)
        {
            var tmp = new StackValue[stack.Length + 1];
            Stack.CopyTo(tmp);
            tmp[^1] = stackValue;
            return new VirtualStack(tmp);
        }

        public VirtualStack Pop(out StackValue stackValue)
        {
            if (stack.Length == 0) { throw new InvalidOperationException(); }
            stackValue = Stack[^1];
            return new VirtualStack(Stack[..^1]);
        }

        public VirtualStack Pop2(out StackValue firstStackValue, out StackValue secondStackValue)
        {
            if (stack.Length <= 1) { throw new InvalidOperationException(); }
            firstStackValue = Stack[^1];
            secondStackValue = Stack[^2];
            return new VirtualStack(Stack[..^2]);
        }

        public VirtualStack PopN(Span<StackValue> outStackValues)
        {
            if (stack.Length < outStackValues.Length) { throw new InvalidOperationException(); }
            for (int i = 0; i < outStackValues.Length; ++i)
            {
                outStackValues[i] = Stack[^i];
            }
            return new VirtualStack(Stack[..^outStackValues.Length]);
        }

        public StackValue Peek()
        {
            return Stack[^1];
        }
    }
}
