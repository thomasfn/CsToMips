using System;
using System.Linq;

namespace CsToMips.MIPS
{
    public enum OperandValueType
    {
        ValueRegister,
        ValueRegisterIndirect,
        DeviceRegister,
        DeviceRegisterIndirect,
        Name,
        Static,
    }

    public enum ValueRegister
    {
        R0,
        R1,
        R2,
        R3,
        R4,
        R5,
        R6,
        R7,
        R8,
        R9,
        R10,
        R11,
        R12,
        R13,
        R14,
        R15,
        SP,
        RA,
    }

    public enum DeviceRegister
    {
        D0,
        D1,
        D2,
        D3,
        D4,
        D5,
        DB,
    }

    public readonly struct OperandValue
    {
        public readonly OperandValueType Type;
        public readonly int IntValue;
        public readonly double RealValue;
        public readonly string TextValue;

        public ValueRegister ValueRegisterValue
        {
            get
            {
                if (Type != OperandValueType.ValueRegister && Type != OperandValueType.ValueRegisterIndirect && Type != OperandValueType.DeviceRegisterIndirect) { throw new InvalidOperationException(); }
                return (ValueRegister)IntValue;
            }
        }

        public DeviceRegister DeviceRegisterValue
        {
            get
            {
                if (Type != OperandValueType.DeviceRegister) { throw new InvalidOperationException(); }
                return (DeviceRegister)IntValue;
            }
        }

        private OperandValue(OperandValueType type, int intValue, double realValue, string textValue)
        {
            Type = type;
            IntValue = intValue;
            RealValue = realValue;
            TextValue = textValue;
        }

        public static OperandValue FromValueRegister(ValueRegister register)
            => new OperandValue(OperandValueType.ValueRegister, (int)register, 0.0, register.ToString().ToLowerInvariant());

        public static OperandValue FromValueRegisterIndirect(ValueRegister register)
            => new OperandValue(OperandValueType.ValueRegisterIndirect, (int)register, 0.0, $"r{register.ToString().ToLowerInvariant()}");

        public static OperandValue FromDeviceRegister(DeviceRegister register)
            => new OperandValue(OperandValueType.DeviceRegister, (int)register, 0.0, register.ToString().ToLowerInvariant());

        public static OperandValue FromDeviceRegisterIndirect(ValueRegister register)
            => new OperandValue(OperandValueType.DeviceRegisterIndirect, (int)register, 0.0, $"d{register.ToString().ToLowerInvariant()}");

        public static OperandValue FromName(string text)
            => new OperandValue(OperandValueType.Name, 0, 0.0, text);

        public static OperandValue FromStatic(double value)
            => new OperandValue(OperandValueType.Name, (int)value, value, value.ToString());

        public static OperandValue Parse(string operand)
        {
            if (string.IsNullOrEmpty(operand)) { throw new ArgumentException("Invalid operand", nameof(operand)); }
            int regIdx;
            if (operand.Length > 1 && operand[0] == 'r' && int.TryParse(operand[1..], out regIdx))
            {
                return FromValueRegister((ValueRegister)regIdx);
            }
            if (operand == "ra") { return FromValueRegister(ValueRegister.RA); }
            if (operand == "sp") { return FromValueRegister(ValueRegister.SP); }
            if (operand.Length > 2 && operand[0] == 'r' && operand[1] == 'r' && int.TryParse(operand[2..], out regIdx))
            {
                return FromValueRegisterIndirect((ValueRegister)regIdx);
            }
            if (operand == "db") { return FromDeviceRegister(DeviceRegister.DB); }
            if (operand.Length > 1 && operand[0] == 'd' && int.TryParse(operand[1..], out regIdx))
            {
                return FromDeviceRegister((DeviceRegister)regIdx);
            }
            if (operand.Length > 2 && operand[0] == 'd' && operand[1] == 'r' && int.TryParse(operand[2..], out regIdx))
            {
                return FromDeviceRegisterIndirect((ValueRegister)regIdx);
            }
            if (double.TryParse(operand, out var realVal))
            {
                return FromStatic(realVal);
            }
            return FromName(operand);
        }

        public override string ToString()
            => TextValue;
    }

    public readonly struct Instruction
    {
        public readonly int LineIndex;
        public readonly OpCode OpCode;
        private readonly OperandValue[] operands;

        public ReadOnlySpan<OperandValue> Operands => operands;

        public Instruction(int lineIndex, OpCode opCode, ReadOnlySpan<OperandValue> operands)
        {
            LineIndex = lineIndex;
            OpCode = opCode;
            this.operands = operands.ToArray();
        }

        public override string ToString()
            => $"{OpCode.GetDescription().Name} {string.Join(" ", operands.Select(o => o.ToString()))}";

        public static Instruction Parse(int lineIndex, string line)
        {
            var words = line.Split(" ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) { throw new ArgumentException("Invalid instruction", nameof(line)); }
            var opCodeStr = words[0];
            var opCodeDesc = OpCodeExt.Find(opCodeStr);
            if (opCodeDesc == null) { throw new ArgumentException("Unknown opcode", nameof(line)); }
            return new Instruction(lineIndex, opCodeDesc.Value.OpCode, words.Skip(1).Select(word => OperandValue.Parse(word)).ToArray());
        }

        public Instruction RewriteBehaviour(OpCodeBehaviour newBehaviour)
        {
            var newOpCodeDesc = OpCodeExt.Find(newBehaviour, OpCode.GetDescription().Condition);
            if (newOpCodeDesc == null) { throw new ArgumentException(nameof(newBehaviour)); }
            return new Instruction(LineIndex, newOpCodeDesc.Value.OpCode, Operands);
        }

        public Instruction SetOperand(Index operandIndex, OperandValue newOperand)
        {
            var newOperands = new OperandValue[Operands.Length];
            newOperands[operandIndex] = newOperand;
            return new Instruction(LineIndex, OpCode, newOperands);
        }
    }
}
