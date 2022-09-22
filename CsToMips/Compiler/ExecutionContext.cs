using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;

namespace CsToMips.Compiler
{
    using Devices;
    using System.Threading;

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    internal sealed class OpCodeHandlerAttribute : Attribute
    {
        public readonly Regex OpCodeNameMatcher;

        public OpCodeHandlerAttribute(string opCodeNameMatcher)
        {
            OpCodeNameMatcher = new Regex($"^{opCodeNameMatcher}$");
        }
    }

    internal readonly struct InstructionHandlerDefinition
    {
        public readonly Regex OpCodeNameMatcher;
        public readonly InstructionHandler Handler;

        public InstructionHandlerDefinition(Regex opCodeNameMatcher, InstructionHandler handler)
        {
            OpCodeNameMatcher = opCodeNameMatcher;
            Handler = handler;
        }
    }

    internal delegate void InstructionHandler(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter);

    internal class ExecutionContext
    {
        private readonly IEnumerable<InstructionHandlerDefinition> instructionHandlers;
        private readonly Stack<StackValue> valueStack;
        private readonly IDictionary<int, StackValue> localVariableStates;
        private RegisterAllocations currentRegisterAllocations;
        private RegisterAllocations allUsedRegisters;
        private readonly RegisterAllocations reservedRegisters;
        private readonly MethodBase method;
        private readonly ISet<MethodInfo> methodDependencies;
        private readonly IList<(int instructionIndex, RegisterAllocations registersInUse, MethodBase method)> callSites;
        private readonly int[] paramIndexToRegister;

        private static readonly IReadOnlyDictionary<MethodBase, string> simpleMethodCompilers = new Dictionary<MethodBase, string>
        {
            { typeof(Math).GetMethod("Abs", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "abs $ #0" },
            { typeof(Math).GetMethod("Acos", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "acos $ #0" },
            { typeof(Math).GetMethod("Asin", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "asin $ #0" },
            { typeof(Math).GetMethod("Atan", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "atan $ #0" },
            { typeof(Math).GetMethod("Ceiling", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "ceil $ #0" },
            { typeof(Math).GetMethod("Cos", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "cos $ #0" },
            { typeof(Math).GetMethod("Exp", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "exp $ #0" },
            { typeof(Math).GetMethod("Floor", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "floor $ #0" },
            { typeof(Math).GetMethod("Log", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "log $ #0" },
            { typeof(Math).GetMethod("Max", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double), typeof(double) })!, "max $ #1 #0" },
            { typeof(Math).GetMethod("Min", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double), typeof(double) })!, "min $ #1 #0" },
            { typeof(Math).GetMethod("Round", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "round $ #0" },
            { typeof(Math).GetMethod("Sin", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "sin $ #0" },
            { typeof(Math).GetMethod("Sqrt", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "sqrt $ #0" },
            { typeof(Math).GetMethod("Tan", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "tan $ #0" },
            { typeof(IC10).GetMethod("Yield", BindingFlags.Public | BindingFlags.Static)!, "yield" },
            { typeof(IC10).GetMethod("Sleep", BindingFlags.Public | BindingFlags.Static, new [] { typeof(float) })!, "sleep #0" },
        };

        public IEnumerable<MethodInfo> MethodDependencies => methodDependencies;

        public ExecutionContext(RegisterAllocations reservedRegisters, MethodBase method)
        {
            instructionHandlers = typeof(ExecutionContext)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => (m, m.GetCustomAttribute<OpCodeHandlerAttribute>()))
                .Where(t => t.Item2 != null)
                .Select(t => new InstructionHandlerDefinition(t.Item2.OpCodeNameMatcher, t.m.CreateDelegate<InstructionHandler>(this)))
                .ToArray();
            valueStack = new Stack<StackValue>();
            localVariableStates = new Dictionary<int, StackValue>();
            this.reservedRegisters = reservedRegisters;
            this.method = method;
            currentRegisterAllocations = reservedRegisters;
            methodDependencies = new HashSet<MethodInfo>();
            callSites = new List<(int instructionIndex, RegisterAllocations registersInUse, MethodBase method)>();
            var ps = method.GetParameters();
            paramIndexToRegister = new int[ps.Length];
            for (int i = 0; i < ps.Length; ++i)
            {
                paramIndexToRegister[i] = AllocateRegister();
            }
        }

        public void Compile(ReadOnlySpan<Instruction> instructions, OutputWriter outputWriter)
        {
            var preamble = new StringBuilder();
            for (int i = paramIndexToRegister.Length - 1; i >= 0; --i)
            {
                preamble.AppendLine($"pop r{paramIndexToRegister[i]}");
            }
            outputWriter.Preamble = preamble.ToString().Trim();
            for (int i = 0; i < instructions.Length; ++i)
            {
                if (ProcessInstruction(instructions, i, outputWriter) == 0)
                {
                    throw new InvalidOperationException($"Unhandled instruction {instructions[i]}");
                }
            }
        }

        private int ProcessInstruction(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            int numHandlers = 0;
            foreach (var handlerDefinition in instructionHandlers)
            {
                if (handlerDefinition.OpCodeNameMatcher.IsMatch(instruction.OpCode.Name ?? ""))
                {
                    handlerDefinition.Handler(instructions, instructionIndex, outputWriter);
                    ++numHandlers;
                }
            }
            return numHandlers;
        }

        [OpCodeHandler(@"nop")]
        private void Handle_Noop(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter) { }

        [OpCodeHandler(@"ldarg(\.[0-9])?")]
        private void Handle_Ldarg(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            if (instruction.OpCode == OpCodes.Ldarg_0 || instruction.OpCode == OpCodes.Ldarg && (int)instruction.Data == 0)
            {
                valueStack.Push(new ThisStackValue());
                return;
            }
            if (instruction.OpCode == OpCodes.Ldarg_1) { valueStack.Push(new RegisterStackValue { RegisterIndex = paramIndexToRegister[0] }); }
            else if (instruction.OpCode == OpCodes.Ldarg_2) { valueStack.Push(new RegisterStackValue { RegisterIndex = paramIndexToRegister[1] }); }
            else if (instruction.OpCode == OpCodes.Ldarg_3) { valueStack.Push(new RegisterStackValue { RegisterIndex = paramIndexToRegister[2] }); }
            else if (instruction.OpCode == OpCodes.Ldarg_S) { valueStack.Push(new RegisterStackValue { RegisterIndex = paramIndexToRegister[(sbyte)instruction.Data] }); }
            else throw new InvalidOperationException();
        }

        [OpCodeHandler(@"ldfld")]
        private void Handle_Ldfld(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var value = valueStack.Pop();
            var fieldInfo = instruction.Data as FieldInfo;
            if (!(value is ThisStackValue) || fieldInfo == null)
            {
                throw new InvalidOperationException($"Can't read fields or properties on unsupported value '${value}'");
            }
            var deviceAttr = fieldInfo.GetCustomAttribute<DeviceAttribute>();
            if (deviceAttr != null)
            {

                valueStack.Push(new DeviceStackValue { PinName = deviceAttr.PinName, DeviceType = fieldInfo.FieldType, Multicast = false });
                return;
            }
            var multicastDeviceAttr = fieldInfo.GetCustomAttribute<MulticastDeviceAttribute>();
            if (multicastDeviceAttr != null)
            {
                valueStack.Push(new DeviceStackValue { PinName = "", DeviceType = fieldInfo.FieldType, Multicast = true });
                return;
            }
            valueStack.Push(new FieldStackValue { UnderlyingField = fieldInfo, AliasName = fieldInfo.Name });
        }

        [OpCodeHandler(@"stfld")]
        private void Handle_Stfld(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var value = valueStack.Pop();
            var target = valueStack.Pop();
            var fieldInfo = instruction.Data as FieldInfo;
            if (!(target is ThisStackValue) || fieldInfo == null)
            {
                throw new InvalidOperationException($"Can't write fields or properties on unsupported value '${value}'");
            }
            var deviceAttr = fieldInfo.GetCustomAttribute<DeviceAttribute>();
            if (deviceAttr != null)
            {
                throw new InvalidOperationException($"Can't write device fields");
            }
            var multicastDeviceAttr = fieldInfo.GetCustomAttribute<MulticastDeviceAttribute>();
            if (multicastDeviceAttr != null)
            {
                throw new InvalidOperationException($"Can't write device fields");
            }
            outputWriter.SetCode(instructionIndex, $"move {fieldInfo.Name} {value.AsIC10}");
        }

        [OpCodeHandler(@"call(virt)?")]
        private void Handle_Call(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var method = instruction.Data as MethodBase;
            if (method == null || method.IsConstructor)
            {
                // This could be a super call on the ctor, for now let's ignore
                return;
            }
            if (simpleMethodCompilers.TryGetValue(method, out string? pattern))
            {
                var ps = method.GetParameters();
                var paramValues = new StackValue[ps.Length];
                for (int i = 0; i < ps.Length; ++i)
                {
                    paramValues[i] = valueStack.Pop();
                    pattern = pattern.Replace($"#{i}", paramValues[i].AsIC10);
                }
                if (method is MethodInfo methodInfo1 && methodInfo1.ReturnType != typeof(void))
                {
                    int regIdx = AllocateRegister();
                    var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                    pattern = pattern.Replace("$", regValue.AsIC10);
                    valueStack.Push(regValue);
                }
                outputWriter.SetCode(instructionIndex, pattern);
                foreach (var v in paramValues)
                {
                    CheckFree(v);
                }
                return;
            }

            if (method.Name.StartsWith("set_"))
            {
                var valueToWrite = valueStack.Pop();
                var callTarget = valueStack.Pop();
                if (callTarget is not DeviceStackValue deviceStackValue)
                {
                    throw new InvalidOperationException($"Can't call methods on unsupported value '${callTarget}'");
                }
                string propertyName = method.Name[4..];
                if (deviceStackValue.Multicast)
                {
                    int typeHash = deviceStackValue.DeviceType.GetCustomAttribute<DeviceInterfaceAttribute>()?.TypeHash ?? 0;
                    outputWriter.SetCode(instructionIndex, $"sb {typeHash} {propertyName} {valueToWrite.AsIC10}");
                }
                else
                {
                    outputWriter.SetCode(instructionIndex, $"s {callTarget.AsIC10} {propertyName} {valueToWrite.AsIC10}");
                }
                CheckFree(valueToWrite);
                return;
            }
            if (method.Name.StartsWith("get_"))
            {
                var callTarget = valueStack.Pop();
                if (callTarget is not DeviceStackValue deviceStackValue)
                {
                    throw new InvalidOperationException($"Can't call methods on unsupported value '${callTarget}'");
                }
                if (deviceStackValue.Multicast) { throw new InvalidOperationException($"Tried to do non-multicast device read on multicast device pin"); }
                string propertyName = method.Name[4..];
                var regIdx = AllocateRegister();
                var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                outputWriter.SetCode(instructionIndex, $"l {regValue.AsIC10} {callTarget.AsIC10} {propertyName}");
                valueStack.Push(regValue);
                return;
            }
            if (method == typeof(IC10).GetMethod("Yield", BindingFlags.Static | BindingFlags.Public))
            {
                outputWriter.SetCode(instructionIndex, $"yield");
                return;
            }
            if (method == typeof(IC10).GetMethod("Sleep", BindingFlags.Static | BindingFlags.Public))
            {
                var value = valueStack.Pop();
                outputWriter.SetCode(instructionIndex, $"sleep {value.AsIC10}");
                CheckFree(value);
                return;
            }
            if (method is MethodInfo methodInfo)
            {
                var ps = method.GetParameters();
                var paramValues = new StackValue[ps.Length];
                for (int i = 0; i < ps.Length; ++i)
                {
                    paramValues[i] = valueStack.Pop();
                }
                var callTarget = method.IsStatic ? null : valueStack.Pop();
                if (callTarget is ThisStackValue)
                {
                    methodDependencies.Add(methodInfo);
                    var sb = new StringBuilder();
                    sb.AppendLine("# CALLSITE ENTRY");
                    sb.AppendLine("push ra");
                    for (int i = 0; i < ps.Length; ++i)
                    {
                        sb.AppendLine($"push {paramValues[i].AsIC10}");
                    }
                    sb.AppendLine($"jal {method.Name}");
                    for (int i = 0; i < ps.Length; ++i)
                    {
                        CheckFree(paramValues[i]);
                    }
                    callSites.Add((instructionIndex, currentRegisterAllocations, method));
                    if (methodInfo.ReturnParameter.ParameterType != typeof(void))
                    {
                        var regIdx = AllocateRegister();
                        var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                        sb.AppendLine($"pop {regValue.AsIC10}");
                        valueStack.Push(regValue);
                    }
                    sb.AppendLine("pop ra");
                    sb.AppendLine("# CALLSITE EXIT");
                    outputWriter.SetCode(instructionIndex, sb.ToString().Trim());
                    return;
                }
                throw new InvalidOperationException($"Can't call unsupported method '{method}'");
            }

            throw new InvalidOperationException($"Can't call unsupported method '{method}'");
        }

        [OpCodeHandler(@"b(r|eq|ge|gt|le|lt|ne|rfalse|rtrue|rzero)(\.un)?(\.s)?")]
        private void Handle_Branch(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            int offset;
            if (instruction.Data is int)
            {
                offset = (int)instruction.Data;
            }
            else if (instruction.Data is sbyte)
            {
                offset = (sbyte)instruction.Data;
            }
            else
            {
                throw new InvalidOperationException();
            }
            var absOffset = instruction.Offset + instruction.Size + offset;
            string? cmd2Arg = null;
            string? cmd1Arg = null;
            if (instruction.OpCode == OpCodes.Beq || instruction.OpCode == OpCodes.Beq_S) { cmd2Arg = "beq"; }
            else if (instruction.OpCode == OpCodes.Bge || instruction.OpCode == OpCodes.Bge_S) { cmd2Arg = "bge"; }
            else if (instruction.OpCode == OpCodes.Bge_Un || instruction.OpCode == OpCodes.Bge_Un_S) { cmd2Arg = "bge"; }
            else if (instruction.OpCode == OpCodes.Bgt || instruction.OpCode == OpCodes.Bgt_S) { cmd2Arg = "bgt"; }
            else if (instruction.OpCode == OpCodes.Bgt_Un || instruction.OpCode == OpCodes.Bgt_Un_S) { cmd2Arg = "bgt"; }
            else if (instruction.OpCode == OpCodes.Ble || instruction.OpCode == OpCodes.Ble_S) { cmd2Arg = "ble"; }
            else if (instruction.OpCode == OpCodes.Ble_Un || instruction.OpCode == OpCodes.Ble_Un_S) { cmd2Arg = "ble"; }
            else if (instruction.OpCode == OpCodes.Blt || instruction.OpCode == OpCodes.Blt_S) { cmd2Arg = "blt"; }
            else if (instruction.OpCode == OpCodes.Blt_Un || instruction.OpCode == OpCodes.Blt_Un_S) { cmd2Arg = "blt"; }
            else if (instruction.OpCode == OpCodes.Bne_Un || instruction.OpCode == OpCodes.Bne_Un_S) { cmd2Arg = "bne"; }
            else if (instruction.OpCode == OpCodes.Brfalse || instruction.OpCode == OpCodes.Brfalse_S) { cmd1Arg = "beqz"; }
            else if (instruction.OpCode == OpCodes.Brtrue || instruction.OpCode == OpCodes.Brtrue_S) { cmd1Arg = "bgtz"; }

            string inst = "j";
            if (!string.IsNullOrEmpty(cmd2Arg))
            {
                var rhs = valueStack.Pop();
                var lhs = valueStack.Pop();
                inst = $"{cmd2Arg} {lhs.AsIC10} {rhs.AsIC10}";
                CheckFree(rhs);
                CheckFree(lhs);
            }
            else if (!string.IsNullOrEmpty(cmd1Arg))
            {
                var lhs = valueStack.Pop();
                inst = $"{cmd1Arg} {lhs.AsIC10}";
                CheckFree(lhs);
            }

            for (int i = 0; i < instructions.Length; ++i)
            {
                if (instructions[i].Offset == absOffset)
                {
                    outputWriter.SetWithLabel(i, true);
                    outputWriter.SetCode(instructionIndex, $"{inst} {outputWriter.GetLabel(i)}");
                    return;
                }
            }
            throw new InvalidOperationException();
        }

        [OpCodeHandler(@"ldc(\.[ir][0-9](\.(m)?[0-9])?)?")]
        private void Handle_Ldc(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            if (instruction.OpCode == OpCodes.Ldc_I4) { valueStack.Push(new StaticStackValue { Value = (int)instruction.Data }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_0) { valueStack.Push(new StaticStackValue { Value = 0 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_1) { valueStack.Push(new StaticStackValue { Value = 1 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_2) { valueStack.Push(new StaticStackValue { Value = 2 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_3) { valueStack.Push(new StaticStackValue { Value = 3 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_4) { valueStack.Push(new StaticStackValue { Value = 4 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_5) { valueStack.Push(new StaticStackValue { Value = 5 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_6) { valueStack.Push(new StaticStackValue { Value = 6 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_7) { valueStack.Push(new StaticStackValue { Value = 7 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_8) { valueStack.Push(new StaticStackValue { Value = 8 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_M1) { valueStack.Push(new StaticStackValue { Value = -1 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_R4) { valueStack.Push(new StaticStackValue { Value = (float)instruction.Data }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_R8) { valueStack.Push(new StaticStackValue { Value = (float)(double)instruction.Data }); return; }
            throw new InvalidOperationException();
        }

        [OpCodeHandler(@"stloc(\.[0-9]|\.s)?")]
        private void Handle_Stloc(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            int localVariableIndex;
            if (instruction.OpCode == OpCodes.Stloc) { localVariableIndex = (int)instruction.Data; }
            else if (instruction.OpCode == OpCodes.Stloc_0) { localVariableIndex = 0; }
            else if (instruction.OpCode == OpCodes.Stloc_1) { localVariableIndex = 1; }
            else if (instruction.OpCode == OpCodes.Stloc_2) { localVariableIndex = 2; }
            else if (instruction.OpCode == OpCodes.Stloc_3) { localVariableIndex = 3; }
            else if (instruction.OpCode == OpCodes.Stloc_S) { localVariableIndex = (sbyte)instruction.Data; }
            else { throw new InvalidOperationException(); }
            localVariableStates[localVariableIndex] = valueStack.Pop();
        }

        [OpCodeHandler(@"ldloc(\.[0-9]|\.s)?")]
        private void Handle_Ldloc(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            int localVariableIndex;
            if (instruction.OpCode == OpCodes.Ldloc) { localVariableIndex = (int)instruction.Data; }
            else if (instruction.OpCode == OpCodes.Ldloc_0) { localVariableIndex = 0; }
            else if (instruction.OpCode == OpCodes.Ldloc_1) { localVariableIndex = 1; }
            else if (instruction.OpCode == OpCodes.Ldloc_2) { localVariableIndex = 2; }
            else if (instruction.OpCode == OpCodes.Ldloc_3) { localVariableIndex = 3; }
            else if (instruction.OpCode == OpCodes.Ldloc_S) { localVariableIndex = (sbyte)instruction.Data; }
            else { throw new InvalidOperationException(); }
            valueStack.Push(localVariableStates[localVariableIndex]);
        }

        [OpCodeHandler(@"add|sub|mul|div|and|or|xor")]
        private void Handle_BinaryArithmetic(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var rhs = valueStack.Pop();
            var lhs = valueStack.Pop();
            string ic10;
            if (instruction.OpCode == OpCodes.Add) { ic10 = "add"; }
            else if (instruction.OpCode == OpCodes.Sub) { ic10 = "sub"; }
            else if (instruction.OpCode == OpCodes.Mul) { ic10 = "mul"; }
            else if (instruction.OpCode == OpCodes.Div) { ic10 = "div"; }
            else if (instruction.OpCode == OpCodes.And) { ic10 = "and"; }
            else if (instruction.OpCode == OpCodes.Or) { ic10 = "or"; }
            else if (instruction.OpCode == OpCodes.Xor) { ic10 = "xor"; }
            else { throw new InvalidOperationException(); }
            var regIdx = AllocateRegister();
            var regValue = new RegisterStackValue { RegisterIndex = regIdx };
            outputWriter.SetCode(instructionIndex, $"{ic10} {regValue.AsIC10} {lhs.AsIC10} {rhs.AsIC10}");
            valueStack.Push(regValue);
            CheckFree(rhs);
            CheckFree(lhs);
        }

        [OpCodeHandler(@"not")]
        private void Handle_UnaryArithmetic(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var rhs = valueStack.Pop();
            string ic10;
            if (instruction.OpCode == OpCodes.Not) { ic10 = "not"; }
            else { throw new InvalidOperationException(); }
            var regIdx = AllocateRegister();
            var regValue = new RegisterStackValue { RegisterIndex = regIdx };
            outputWriter.SetCode(instructionIndex, $"{ic10} {regValue.AsIC10} {rhs.AsIC10}");
            valueStack.Push(regValue);
            CheckFree(rhs);
        }

        [OpCodeHandler(@"(ceq|cgt|clt)(\.un)?")]
        private void Handle_Compare(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var rhs = valueStack.Pop();
            var lhs = valueStack.Pop();
            string ic10;
            if (instruction.OpCode == OpCodes.Ceq) { ic10 = "seq"; }
            else if (instruction.OpCode == OpCodes.Cgt) { ic10 = "sgt"; }
            else if (instruction.OpCode == OpCodes.Cgt_Un) { ic10 = "sgt"; }
            else if (instruction.OpCode == OpCodes.Clt) { ic10 = "slt"; }
            else if (instruction.OpCode == OpCodes.Clt_Un) { ic10 = "slt"; }
            else { throw new InvalidOperationException(); }
            var regIdx = AllocateRegister();
            var regValue = new RegisterStackValue { RegisterIndex = regIdx };
            outputWriter.SetCode(instructionIndex, $"{ic10} {regValue.AsIC10} {lhs.AsIC10} {rhs.AsIC10}");
            valueStack.Push(regValue);
            CheckFree(rhs);
            CheckFree(lhs);
        }

        [OpCodeHandler(@"switch")]
        private void Handle_Switch(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var switchValue = valueStack.Pop();
            int[]? offsets = instruction.Data as int[];
            if (offsets == null) { throw new InvalidOperationException(); }
            var sb = new StringBuilder();
            for (int j = 0; j < offsets.Length; ++j)
            {
                var offset = offsets[j];
                var absOffset = instruction.Offset + instruction.Size + offset;
                bool found = false;
                for (int i = 0; i < instructions.Length; ++i)
                {
                    if (instructions[i].Offset == absOffset)
                    {
                        outputWriter.SetWithLabel(i, true);
                        sb.AppendLine($"beq {switchValue.AsIC10} {j} {outputWriter.GetLabel(i)}");
                        found = true;
                        break;
                    }
                }
                if (!found) { throw new InvalidOperationException(); }
            }
            outputWriter.SetCode(instructionIndex, sb.ToString().Trim());
            CheckFree(switchValue);
        }

        [OpCodeHandler(@"ret")]
        private void Handle_Ret(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            if (method.IsConstructor || method is not MethodInfo methodInfo) { return; }
            if (methodInfo.ReturnType == typeof(void))
            {
                outputWriter.SetCode(instructionIndex, $"j ra");
                return;
            }
            var retVal = valueStack.Pop();
            outputWriter.SetCode(instructionIndex, $"push {retVal.AsIC10}\nj ra");
            CheckFree(retVal);
        }

        [OpCodeHandler(@"conv\..+")]
        private void Handle_Conv(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            // since ic10 only has f32 registers, the only conv we're interested in emitting for is f -> i
            var iname = instruction.OpCode.Name ?? "";
            if (iname.StartsWith("conv.i") || iname.StartsWith("conv.u"))
            {
                var value = valueStack.Pop();
                var regIdx = AllocateRegister();
                var regVal = new RegisterStackValue { RegisterIndex = regIdx };
                outputWriter.SetCode(instructionIndex, $"trunc {regVal.AsIC10} {value.AsIC10}");
                valueStack.Push(regVal);
                CheckFree(value);
            }
        }

        public void FixupCallSites(IReadOnlyDictionary<MethodBase, (ExecutionContext, OutputWriter)> contexts, OutputWriter outputWriter)
        {
            foreach (var (instuctionIndex, registersInUse, method) in callSites)
            {
                var context = contexts[method];
                var conflictingRegisters = context.Item1.allUsedRegisters & registersInUse & ~reservedRegisters;
                var callSiteEntry = new StringBuilder();
                var callSiteExit = new StringBuilder();
                for (int i = 0; i < RegisterAllocations.NumTotal; ++i)
                {
                    if (conflictingRegisters.IsAllocated(i))
                    {
                        callSiteEntry.AppendLine($"push r{i}");
                    }
                    if (conflictingRegisters.IsAllocated(RegisterAllocations.NumTotal - (i + 1)))
                    {
                        callSiteExit.AppendLine($"pop r{RegisterAllocations.NumTotal - (i + 1)}");
                    }
                }
                outputWriter.SetCode(
                    instuctionIndex,
                    (outputWriter[instuctionIndex].Code ?? "")
                        .Replace("# CALLSITE ENTRY", callSiteEntry.ToString().Trim())
                        .Replace("# CALLSITE EXIT", callSiteExit.ToString().Trim())
                        .Trim()
                );
            }
        }

        private int AllocateRegister()
        {
            currentRegisterAllocations = currentRegisterAllocations.Allocate(out int regIdx);
            allUsedRegisters |= currentRegisterAllocations;
            return regIdx;
        }

        private void CheckFree(StackValue stackValue)
        {
            if (stackValue is not RegisterStackValue registerStackValue) { return; }
            int refCount = FindRegisterReferences(registerStackValue.RegisterIndex);
            if (refCount > 0) { return; }
            currentRegisterAllocations = currentRegisterAllocations.Free(registerStackValue.RegisterIndex);
        }

        private int FindRegisterReferences(int registerIndex)
        {
            int refCount = 0;
            if (reservedRegisters.IsAllocated(registerIndex)) { ++refCount; }
            foreach (var item in valueStack)
            {
                if (item is RegisterStackValue registerStackValue && registerStackValue.RegisterIndex == registerIndex) { ++refCount; }
            }
            foreach (var keyPair in localVariableStates)
            {
                if (keyPair.Value is RegisterStackValue registerStackValue && registerStackValue.RegisterIndex == registerIndex) { ++refCount; }
            }
            return refCount;
        }

        private void PushStateToStack(StringBuilder sb)
        {
            for (int i = 0; i < 16; ++i)
            {
                if (currentRegisterAllocations.IsAllocated(i) && !reservedRegisters.IsAllocated(i))
                {
                    sb.AppendLine($"push r{i}");
                }
            }
            sb.AppendLine($"push ra");
        }

        private void PopStateFromStack(StringBuilder sb)
        {
            sb.AppendLine($"pop ra");
            for (int i = 15; i >= 0; --i)
            {
                if (currentRegisterAllocations.IsAllocated(i) && !reservedRegisters.IsAllocated(i))
                {
                    sb.AppendLine($"pop r{i}");
                }
            }
        }
    }
}
