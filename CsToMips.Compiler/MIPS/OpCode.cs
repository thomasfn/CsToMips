using System;
using System.Linq;

namespace CsToMips.MIPS
{
    public enum OperandType
    {
        Value,
        StaticValue,
        DeviceValue,
        ValueRegister,
        AnyRegister,
        VariableName,
        LabelName,
        JumpTarget,
        BatchMode
    }

    public readonly struct OperandDescription
    {
        public readonly OperandType Type;

        public OperandDescription(OperandType type)
        {
            Type = type;
        }

        public override string ToString()
        {
            switch (Type)
            {
                case OperandType.Value: return "r?|n";
                case OperandType.StaticValue: return "n";
                case OperandType.DeviceValue: return "d?";
                case OperandType.ValueRegister: return "r?";
                case OperandType.AnyRegister: return "r?|d?";
                case OperandType.VariableName: return "v";
                case OperandType.LabelName: return "l";
                case OperandType.JumpTarget: return "l|n";
                case OperandType.BatchMode: return "bm";
                default: return "";
            }
        }
    }

    public enum OpCodeCondition
    {
        Unconditional,
        GreaterThan,
        GreaterThanZero,
        GreaterThanOrEqual,
        GreaterThanOrEqualZero,
        LessThan,
        LessThanZero,
        LessThanOrEqual,
        LessThanOrEqualZero,
        Equal,
        EqualZero,
        NotEqual,
        NotEqualZero,
        DeviceSet,
        DeviceNotSet,
        ApproxEqual,
        ApproxEqualZero,
        ApproxNotEqual,
        ApproxNotEqualZero,
    }

    public enum OpCodeBehaviour
    {
        Jump,
        JumpWithReturn,
        RelativeJump,
        SetRegister,
        Arithmetic,
        Meta,
        Stack,
        DeviceInterop,
        Timing,
        Other,
    }

    public readonly struct OpCodeDescription
    {
        public readonly OpCode OpCode;
        public readonly string Name;
        private readonly OperandDescription[] operands;
        public ReadOnlySpan<OperandDescription> Operands => operands;
        public readonly OpCodeCondition Condition;
        public readonly OpCodeBehaviour Behaviour;

        public bool IsJump => Behaviour == OpCodeBehaviour.Jump || Behaviour == OpCodeBehaviour.JumpWithReturn || Behaviour == OpCodeBehaviour.RelativeJump;

        public bool IsConditional => Condition != OpCodeCondition.Unconditional;

        public OpCodeDescription(OpCode opCode, string name, OpCodeCondition condition, OpCodeBehaviour behaviour, ReadOnlySpan<OperandDescription> operands)
        {
            OpCode = opCode;
            Name = name;
            this.operands = operands.ToArray();
            Condition = condition;
            Behaviour = behaviour;
        }

        public OpCodeDescription(OpCode opCode, string name, OpCodeCondition condition, OpCodeBehaviour behaviour, ReadOnlySpan<OperandType> operands)
        {
            OpCode = opCode;
            Name = name;
            this.operands = new OperandDescription[operands.Length];
            for (int i = 0; i < operands.Length; ++i)
            {
                this.operands[i] = new OperandDescription(operands[i]);
            }
            Condition = condition;
            Behaviour = behaviour;
        }

        public override string ToString()
            => $"{Name} {string.Join(" ", operands.Select(od => od.ToString()))}";
    }

    public enum OpCode
    {
        L,
        S,
        Ls,
        Lr,
        Sb,
        Lb,
        Alias,
        Move,
        Add,
        Sub,
        Sdse,
        Sdns,
        Slt,
        Sgt,
        Sle,
        Sge,
        Seq,
        Sne,
        Sap,
        Sna,
        And,
        Or,
        Xor,
        Nor,
        Mul,
        Div,
        Mod,
        J,
        Bltz,
        Bgez,
        Blez,
        Bgtz,
        Bdse,
        Bdns,
        Beq,
        Bne,
        Bap,
        Bna,
        Jal,
        Brdse,
        Brdns,
        Bltzal,
        Bgezal,
        Blezal,
        Bgtzal,
        Beqal,
        Bneal,
        Jr,
        Bdseal,
        Bdnsal,
        Brltz,
        Brgez,
        Brlez,
        Brgtz,
        Breq,
        Brne,
        Brap,
        Brna,
        Sqrt,
        Round,
        Trunc,
        Ceil,
        Floor,
        Max,
        Min,
        Abs,
        Log,
        Exp,
        Rand,
        Yield,
        Label,
        Peek,
        Push,
        Pop,
        Hcf,
        Select,
        Blt,
        Bgt,
        Ble,
        Bge,
        Brlt,
        Brgt,
        Brle,
        Brge,
        Bltal,
        Bgtal,
        Bleal,
        Bgeal,
        Bapal,
        Bnaal,
        Beqz,
        Bnez,
        Bapz,
        Bnaz,
        Breqz,
        Brnez,
        Brapz,
        Brnaz,
        Beqzal,
        Bnezal,
        Bapzal,
        Bnazal,
        Sltz,
        Sgtz,
        Slez,
        Sgez,
        Seqz,
        Snez,
        Sapz,
        Snaz,
        Define,
        Sleep,
        Sin,
        Asin,
        Tan,
        Atan,
        Cos,
        Acos,
        Atan2,
    }

    public static class OpCodeExt
    {
        private static readonly OpCodeDescription[] opCodeDescriptions = new[]
        {
            // -- Device Interop --
            /* S */     new OpCodeDescription(OpCode.S,     "s",        OpCodeCondition.Unconditional,          OpCodeBehaviour.DeviceInterop,  new [] { OperandType.DeviceValue, OperandType.VariableName, OperandType.Value }),
            /* L */     new OpCodeDescription(OpCode.L,     "l",        OpCodeCondition.Unconditional,          OpCodeBehaviour.DeviceInterop,  new [] { OperandType.ValueRegister, OperandType.DeviceValue, OperandType.VariableName }),
            /* Ls */    new OpCodeDescription(OpCode.Ls,    "ls",       OpCodeCondition.Unconditional,          OpCodeBehaviour.DeviceInterop,  new [] { OperandType.ValueRegister, OperandType.DeviceValue, OperandType.StaticValue, OperandType.VariableName }),
            /* Lr */    new OpCodeDescription(OpCode.Lr,    "lr",       OpCodeCondition.Unconditional,          OpCodeBehaviour.DeviceInterop,  new [] { OperandType.ValueRegister, OperandType.DeviceValue, OperandType.StaticValue, OperandType.StaticValue }),
            /* Sb */    new OpCodeDescription(OpCode.Sb,    "sb",       OpCodeCondition.Unconditional,          OpCodeBehaviour.DeviceInterop,  new [] { OperandType.StaticValue, OperandType.VariableName, OperandType.Value }),
            /* Lb */    new OpCodeDescription(OpCode.Lb,    "lb",       OpCodeCondition.Unconditional,          OpCodeBehaviour.DeviceInterop,  new [] { OperandType.ValueRegister, OperandType.StaticValue, OperandType.VariableName, OperandType.BatchMode }),

            // -- Meta / Preprocessor --
            /* Alias */ new OpCodeDescription(OpCode.Alias, "alias",    OpCodeCondition.Unconditional,          OpCodeBehaviour.Meta,           new [] { OperandType.LabelName, OperandType.AnyRegister }),
            /* Yield */ new OpCodeDescription(OpCode.Yield, "yield",    OpCodeCondition.Unconditional,          OpCodeBehaviour.Meta,           ReadOnlySpan<OperandDescription>.Empty),
            /* Sleep */ new OpCodeDescription(OpCode.Sleep, "sleep",    OpCodeCondition.Unconditional,          OpCodeBehaviour.Meta,           new [] { OperandType.Value }),
            
            // -- Set Register --
            /* Move */  new OpCodeDescription(OpCode.Move,  "move",     OpCodeCondition.Unconditional,          OpCodeBehaviour.Other,          new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Seq */   new OpCodeDescription(OpCode.Seq,   "seq",      OpCodeCondition.Equal,                  OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Seqz */  new OpCodeDescription(OpCode.Seqz,  "seqz",     OpCodeCondition.EqualZero,              OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Sge */   new OpCodeDescription(OpCode.Sge,   "sge",      OpCodeCondition.GreaterThanOrEqual,     OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Sgez */  new OpCodeDescription(OpCode.Sgez,  "sgez",     OpCodeCondition.GreaterThanOrEqualZero, OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Sgt */   new OpCodeDescription(OpCode.Sgt,   "sgt",      OpCodeCondition.GreaterThan,            OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Sgtz */  new OpCodeDescription(OpCode.Sgtz,  "sgtz",     OpCodeCondition.GreaterThanZero,        OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Sle */   new OpCodeDescription(OpCode.Sle,   "sle",      OpCodeCondition.LessThanOrEqual,        OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Slez */  new OpCodeDescription(OpCode.Slez,  "slez",     OpCodeCondition.LessThanOrEqualZero,    OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Slt */   new OpCodeDescription(OpCode.Slt,   "slt",      OpCodeCondition.LessThan,               OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Sltz */  new OpCodeDescription(OpCode.Sltz,  "sltz",     OpCodeCondition.LessThanZero,           OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Sne */   new OpCodeDescription(OpCode.Sne,   "sne",      OpCodeCondition.NotEqual,               OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Snez */  new OpCodeDescription(OpCode.Snez,  "snez",     OpCodeCondition.NotEqualZero,           OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Sdns */  new OpCodeDescription(OpCode.Sdns,  "sdns",     OpCodeCondition.DeviceNotSet,           OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.DeviceValue }),
            /* Sdse */  new OpCodeDescription(OpCode.Sdse,  "sdse",     OpCodeCondition.DeviceSet,              OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.DeviceValue }),
            /* Sap */   new OpCodeDescription(OpCode.Sap,   "sap",      OpCodeCondition.ApproxEqual,            OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Sapz */  new OpCodeDescription(OpCode.Sapz,  "sapz",     OpCodeCondition.ApproxEqualZero,        OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            /* Sna */   new OpCodeDescription(OpCode.Sna,   "sna",      OpCodeCondition.ApproxNotEqual,         OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Snaz */  new OpCodeDescription(OpCode.Snaz,  "snaz",     OpCodeCondition.ApproxNotEqualZero,     OpCodeBehaviour.SetRegister,    new [] { OperandType.ValueRegister, OperandType.Value }),
            
            // -- Branch to line --
            /* J */     new OpCodeDescription(OpCode.J,     "j",        OpCodeCondition.Unconditional,          OpCodeBehaviour.Jump,           new [] { OperandType.JumpTarget }),
            /* Beq */   new OpCodeDescription(OpCode.Beq,   "beq",      OpCodeCondition.Equal,                  OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Beqz */  new OpCodeDescription(OpCode.Beqz,  "beqz",     OpCodeCondition.EqualZero,              OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bge */   new OpCodeDescription(OpCode.Bge,   "bge",      OpCodeCondition.GreaterThanOrEqual,     OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bgez */  new OpCodeDescription(OpCode.Bgez,  "bgez",     OpCodeCondition.GreaterThanOrEqualZero, OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bgt */   new OpCodeDescription(OpCode.Bgt,   "bgt",      OpCodeCondition.GreaterThan,            OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bgtz */  new OpCodeDescription(OpCode.Bgtz,  "bgtz",     OpCodeCondition.GreaterThanZero,        OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Ble */   new OpCodeDescription(OpCode.Ble,   "ble",      OpCodeCondition.LessThanOrEqual,        OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Blez */  new OpCodeDescription(OpCode.Blez,  "blez",     OpCodeCondition.LessThanOrEqualZero,    OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Blt */   new OpCodeDescription(OpCode.Blt,   "blt",      OpCodeCondition.LessThan,               OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bltz */  new OpCodeDescription(OpCode.Bltz,  "bltz",     OpCodeCondition.LessThanZero,           OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bne */   new OpCodeDescription(OpCode.Bne,   "bne",      OpCodeCondition.NotEqual,               OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bnez */  new OpCodeDescription(OpCode.Bnez,  "bnez",     OpCodeCondition.NotEqualZero,           OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bdns */  new OpCodeDescription(OpCode.Bdns,  "bdns",     OpCodeCondition.DeviceNotSet,           OpCodeBehaviour.Jump,           new [] { OperandType.DeviceValue, OperandType.JumpTarget }),
            /* Bdse */  new OpCodeDescription(OpCode.Bdse,  "bdse",     OpCodeCondition.DeviceSet,              OpCodeBehaviour.Jump,           new [] { OperandType.DeviceValue, OperandType.JumpTarget }),
            /* Bap */   new OpCodeDescription(OpCode.Bap,   "bap",      OpCodeCondition.ApproxEqual,            OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bapz */  new OpCodeDescription(OpCode.Bapz,  "bapz",     OpCodeCondition.ApproxEqualZero,        OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bna */   new OpCodeDescription(OpCode.Bna,   "bna",      OpCodeCondition.ApproxNotEqual,         OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bnaz */  new OpCodeDescription(OpCode.Bnaz,  "bnaz",     OpCodeCondition.ApproxNotEqualZero,     OpCodeBehaviour.Jump,           new [] { OperandType.Value, OperandType.JumpTarget }),
            
            // -- Branch and store return address --
            /* Jal */       new OpCodeDescription(OpCode.Jal,       "jal",      OpCodeCondition.Unconditional,          OpCodeBehaviour.JumpWithReturn, new [] { OperandType.JumpTarget }),
            /* Beqal */     new OpCodeDescription(OpCode.Beqal,     "beqal",    OpCodeCondition.Equal,                  OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Beqzal */    new OpCodeDescription(OpCode.Beqzal,    "beqzal",   OpCodeCondition.EqualZero,              OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bgeal */     new OpCodeDescription(OpCode.Bgeal,     "bgeal",    OpCodeCondition.GreaterThanOrEqual,     OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bgezal */    new OpCodeDescription(OpCode.Bgezal,    "bgezal",   OpCodeCondition.GreaterThanOrEqualZero, OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bgtal */     new OpCodeDescription(OpCode.Bgtal,     "bgtal",    OpCodeCondition.GreaterThan,            OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bgtzal */    new OpCodeDescription(OpCode.Bgtzal,    "bgtzal",   OpCodeCondition.GreaterThanZero,        OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bleal */     new OpCodeDescription(OpCode.Bleal,     "bleal",    OpCodeCondition.LessThanOrEqual,        OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Blezal */    new OpCodeDescription(OpCode.Blezal,    "blezal",   OpCodeCondition.LessThanOrEqualZero,    OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bltal */     new OpCodeDescription(OpCode.Bltal,     "bltal",    OpCodeCondition.LessThan,               OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bltzal */    new OpCodeDescription(OpCode.Bltzal,    "bltzal",   OpCodeCondition.LessThanZero,           OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bneal */     new OpCodeDescription(OpCode.Bneal,     "bneal",    OpCodeCondition.NotEqual,               OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bnezal */    new OpCodeDescription(OpCode.Bnezal,    "bnezal",   OpCodeCondition.NotEqualZero,           OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bdnsal */    new OpCodeDescription(OpCode.Bdnsal,    "bdnsal",   OpCodeCondition.DeviceNotSet,           OpCodeBehaviour.JumpWithReturn, new [] { OperandType.DeviceValue, OperandType.JumpTarget }),
            /* Bdseal */    new OpCodeDescription(OpCode.Bdseal,    "bdseal",   OpCodeCondition.DeviceSet,              OpCodeBehaviour.JumpWithReturn, new [] { OperandType.DeviceValue, OperandType.JumpTarget }),
            /* Bapal */     new OpCodeDescription(OpCode.Bapal,     "bapal",    OpCodeCondition.ApproxEqual,            OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bapzal */    new OpCodeDescription(OpCode.Bapzal,    "bapzal",   OpCodeCondition.ApproxEqualZero,        OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),
            /* Bnaal */     new OpCodeDescription(OpCode.Bnaal,     "bnaal",    OpCodeCondition.ApproxNotEqual,         OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.Value, OperandType.JumpTarget }),
            /* Bnazal */    new OpCodeDescription(OpCode.Bnazal,    "bnazal",   OpCodeCondition.ApproxNotEqualZero,     OpCodeBehaviour.JumpWithReturn, new [] { OperandType.Value, OperandType.JumpTarget }),

            // -- Relative jump to line --
            /* Jr */     new OpCodeDescription(OpCode.Jr,       "jr",        OpCodeCondition.Unconditional,          OpCodeBehaviour.RelativeJump,      new [] { OperandType.StaticValue }),
            /* Breq */   new OpCodeDescription(OpCode.Breq,     "breq",      OpCodeCondition.Equal,                  OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Breqz */  new OpCodeDescription(OpCode.Breqz,    "breqz",     OpCodeCondition.EqualZero,              OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brge */   new OpCodeDescription(OpCode.Brge,     "brge",      OpCodeCondition.GreaterThanOrEqual,     OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brgez */  new OpCodeDescription(OpCode.Brgez,    "brgez",     OpCodeCondition.GreaterThanOrEqualZero, OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brgt */   new OpCodeDescription(OpCode.Brgt,     "brgt",      OpCodeCondition.GreaterThan,            OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brgtz */  new OpCodeDescription(OpCode.Brgtz,    "brgtz",     OpCodeCondition.GreaterThanZero,        OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brle */   new OpCodeDescription(OpCode.Brle,     "brle",      OpCodeCondition.LessThanOrEqual,        OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brlez */  new OpCodeDescription(OpCode.Brlez,    "brlez",     OpCodeCondition.LessThanOrEqualZero,    OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brlt */   new OpCodeDescription(OpCode.Brlt,     "brlt",      OpCodeCondition.LessThan,               OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brltz */  new OpCodeDescription(OpCode.Brltz,    "brltz",     OpCodeCondition.LessThanZero,           OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brne */   new OpCodeDescription(OpCode.Brne,     "brne",      OpCodeCondition.NotEqual,               OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brnez */  new OpCodeDescription(OpCode.Brnez,    "brnez",     OpCodeCondition.NotEqualZero,           OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brdns */  new OpCodeDescription(OpCode.Brdns,    "brdns",     OpCodeCondition.DeviceNotSet,           OpCodeBehaviour.RelativeJump,      new [] { OperandType.DeviceValue, OperandType.StaticValue }),
            /* Brdse */  new OpCodeDescription(OpCode.Brdse,    "brdse",     OpCodeCondition.DeviceSet,              OpCodeBehaviour.RelativeJump,      new [] { OperandType.DeviceValue, OperandType.StaticValue }),
            /* Brap */   new OpCodeDescription(OpCode.Brap,     "brap",      OpCodeCondition.ApproxEqual,            OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brapz */  new OpCodeDescription(OpCode.Brapz,    "brapz",     OpCodeCondition.ApproxEqualZero,        OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),
            /* Brna */   new OpCodeDescription(OpCode.Brna,     "brna",      OpCodeCondition.ApproxNotEqual,         OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.Value, OperandType.StaticValue }),
            /* Brnaz */  new OpCodeDescription(OpCode.Brnaz,    "brnaz",     OpCodeCondition.ApproxNotEqualZero,     OpCodeBehaviour.RelativeJump,      new [] { OperandType.Value, OperandType.StaticValue }),

            // -- Arithmetic --
            /* Add */   new OpCodeDescription(OpCode.Add,   "add",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Sub */   new OpCodeDescription(OpCode.Sub,   "sub",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* And */   new OpCodeDescription(OpCode.And,   "and",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Or */    new OpCodeDescription(OpCode.Or,    "or",       OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Xor */   new OpCodeDescription(OpCode.Xor,   "xor",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Nor */   new OpCodeDescription(OpCode.Nor,   "nor",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Mul */   new OpCodeDescription(OpCode.Mul,   "mul",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Div */   new OpCodeDescription(OpCode.Div,   "div",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Mod */   new OpCodeDescription(OpCode.Mod,   "mod",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Max */   new OpCodeDescription(OpCode.Max,   "max",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),
            /* Min */   new OpCodeDescription(OpCode.Min,   "min",      OpCodeCondition.Unconditional,          OpCodeBehaviour.Arithmetic,     new [] { OperandType.ValueRegister, OperandType.Value, OperandType.Value }),

            
            
            ///* Sqrt, */ new OpCodeDescription(OpCode.Sqrt, ),
            ///* Round, */ new OpCodeDescription(OpCode.Round, ),
            ///* Trunc, */ new OpCodeDescription(OpCode.Trunc, ),
            ///* Ceil, */ new OpCodeDescription(OpCode.Ceil, ),
            ///* Floor, */ new OpCodeDescription(OpCode.Floor, ),
            ///* Max, */ new OpCodeDescription(OpCode.Max, ),
            ///* Min, */ new OpCodeDescription(OpCode.Min, ),
            ///* Abs, */ new OpCodeDescription(OpCode.Abs, ),
            ///* Log, */ new OpCodeDescription(OpCode.Log, ),
            ///* Exp, */ new OpCodeDescription(OpCode.Exp, ),
            ///* Rand, */ new OpCodeDescription(OpCode.Rand, ),
            ///* Yield, */ new OpCodeDescription(OpCode.Yield, ),
            ///* Label, */ new OpCodeDescription(OpCode.Label, ),
            ///* Peek, */ new OpCodeDescription(OpCode.Peek, ),
            ///* Push, */ new OpCodeDescription(OpCode.Push, ),
            ///* Pop, */ new OpCodeDescription(OpCode.Pop, ),
            ///* Hcf, */ new OpCodeDescription(OpCode.Hcf, ),
            ///* Select, */ new OpCodeDescription(OpCode.Select, ),
            
            ///* Define, */ new OpCodeDescription(OpCode.Define, ),
            ///* Sleep, */ new OpCodeDescription(OpCode.Sleep, ),
            ///* Sin, */ new OpCodeDescription(OpCode.Sin, ),
            ///* Asin, */ new OpCodeDescription(OpCode.Asin, ),
            ///* Tan, */ new OpCodeDescription(OpCode.Tan, ),
            ///* Atan, */ new OpCodeDescription(OpCode.Atan, ),
            ///* Cos, */ new OpCodeDescription(OpCode.Cos, ),
            ///* Acos, */ new OpCodeDescription(OpCode.Acos, ),
            ///* Atan2, */ new OpCodeDescription(OpCode.Atan2, ),
        };

        private static readonly OpCodeDescription[] opCodeToOpCodeDescription;

        static OpCodeExt()
        {
            var opCodes = Enum.GetValues<OpCode>();
            opCodeToOpCodeDescription = new OpCodeDescription[opCodes.Length];
            foreach (var description in opCodeDescriptions)
            {
                opCodeToOpCodeDescription[(int)description.OpCode] = description;
            }
        }

        public static OpCodeDescription GetDescription(this OpCode opCode)
            => opCodeToOpCodeDescription[(int)opCode];

        public static OpCodeDescription? Find(string name)
        {
            for (int i = 0; i < opCodeDescriptions.Length; ++i)
            {
                if (opCodeDescriptions[i].Name == name)
                {
                    return opCodeDescriptions[i];
                }
            }
            return null;
        }

        public static OpCodeDescription? Find(OpCodeBehaviour behaviour, OpCodeCondition condition)
        {
            for (int i = 0; i < opCodeDescriptions.Length; ++i)
            {
                if (opCodeDescriptions[i].Behaviour == behaviour && opCodeDescriptions[i].Condition == condition)
                {
                    return opCodeDescriptions[i];
                }
            }
            return null;
        }
    }
}
