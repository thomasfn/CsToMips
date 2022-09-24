using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;

namespace CsToMips.Compiler
{
    using Devices;

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

    internal readonly struct CallSite
    {
        public readonly int InstructionIndex;
        public readonly MethodBase TargetMethod;
        public readonly StackValue[] CallParams;
        public readonly StackValue? ReturnParam;
        public readonly RegisterAllocations RegistersState;
        public readonly StackValue[] ValueStackState;

        public CallSite(int instructionIndex, MethodBase targetMethod, StackValue[] callParams, StackValue? returnParam, RegisterAllocations registersState, StackValue[] valueStackState)
        {
            InstructionIndex = instructionIndex;
            TargetMethod = targetMethod;
            CallParams = callParams;
            ReturnParam = returnParam;
            RegistersState = registersState;
            ValueStackState = valueStackState;
        }
    }

    internal class ExecutionContext
    {
        private readonly IEnumerable<InstructionHandlerDefinition> instructionHandlers;
        private readonly Stack<StackValue> valueStack;
        private readonly IDictionary<int, StackValue> localVariableStates;
        private RegisterAllocations currentRegisterAllocations;
        private RegisterAllocations allUsedRegisters;
        private readonly RegisterAllocations reservedRegisters;
        private readonly MethodBase method;
        private readonly ISet<MethodBase> methodDependencies;
        private readonly IList<CallSite> callSites;
        private readonly StackValue[] paramIndexToStackValue;
        private readonly StackValue? returnStackValue;
        private readonly bool isInline;

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
        };

        public IEnumerable<MethodBase> MethodDependencies => methodDependencies;

        public ExecutionContext(RegisterAllocations reservedRegisters, MethodBase method, bool isInline = false, IEnumerable<StackValue>? initialStackState = null, StackValue? returnStackValue = null)
        {
            instructionHandlers = typeof(ExecutionContext)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => (m, m.GetCustomAttribute<OpCodeHandlerAttribute>()))
                .Where(t => t.Item2 != null)
                .Select(t => new InstructionHandlerDefinition(t.Item2.OpCodeNameMatcher, t.m.CreateDelegate<InstructionHandler>(this)))
                .ToArray();
            valueStack = new Stack<StackValue>(initialStackState ?? Enumerable.Empty<StackValue>());
            localVariableStates = new Dictionary<int, StackValue>();
            this.reservedRegisters = reservedRegisters;
            this.method = method;
            currentRegisterAllocations = reservedRegisters;
            methodDependencies = new HashSet<MethodBase>();
            callSites = new List<CallSite>();
            var ps = method.GetParameters();
            paramIndexToStackValue = new RegisterStackValue[ps.Length];
            if (isInline)
            {
                for (int i = 0; i < ps.Length; ++i)
                {
                    paramIndexToStackValue[i] = valueStack.Pop();
                    if (paramIndexToStackValue[i] is RegisterStackValue registerStackValue) { currentRegisterAllocations = currentRegisterAllocations.Allocate(registerStackValue.RegisterIndex); }
                }
            }
            else
            {
                for (int i = 0; i < ps.Length; ++i)
                {
                    paramIndexToStackValue[i] = new RegisterStackValue { RegisterIndex = AllocateRegister() };
                }
            }
            this.isInline = isInline;
            this.returnStackValue = returnStackValue;
        }

        public void Compile(ReadOnlySpan<Instruction> instructions, OutputWriter outputWriter)
        {
            if (isInline)
            {
                outputWriter.Postamble = $"{outputWriter.LabelPrefix}_end:";
            }
            else
            {
                var preamble = new StringBuilder();
                for (int i = 0; i < paramIndexToStackValue.Length; ++i)
                {
                    preamble.AppendLine($"pop {paramIndexToStackValue[i].AsIC10}");
                }
                outputWriter.Preamble = preamble.ToString().Trim();
            }
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
            if (instruction.OpCode == OpCodes.Ldarg_1) { valueStack.Push(paramIndexToStackValue[0]); }
            else if (instruction.OpCode == OpCodes.Ldarg_2) { valueStack.Push(paramIndexToStackValue[1]); }
            else if (instruction.OpCode == OpCodes.Ldarg_3) { valueStack.Push(paramIndexToStackValue[2]); }
            else if (instruction.OpCode == OpCodes.Ldarg_S) { valueStack.Push(paramIndexToStackValue[(sbyte)instruction.Data]); }
            else throw new InvalidOperationException();
        }

        [OpCodeHandler(@"ldstr")]
        private void Handle_Ldstr(ReadOnlySpan<Instruction> instructions, int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            valueStack.Push(new StringStackValue { Value = (string)instruction.Data });
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
            var callParams = GetCallParameters(method);
            var callTarget = method.IsStatic ? null : valueStack.Pop();
            CompileHintCallType compileHintCallType;
            string? compileHintPattern;
            var compileHintAttr = method.GetCustomAttribute<CompileHintAttribute>();
            if (simpleMethodCompilers.TryGetValue(method, out string? pattern))
            {
                compileHintCallType = CompileHintCallType.Inline;
                compileHintPattern = pattern;
            }
            else if (compileHintAttr != null)
            {
                compileHintCallType = compileHintAttr.CompileHintCallType;
                compileHintPattern = compileHintAttr.Pattern;
            }
            else
            {
                compileHintCallType = CompileHintCallType.Inline;
                compileHintPattern = null;
            }
            if (!string.IsNullOrEmpty(compileHintPattern))
            {
                var substitutedPattern = compileHintPattern;
                
                if (compileHintCallType == CompileHintCallType.Inline)
                {
                    for (int i = 0; i < callParams.Length; ++i)
                    {
                        substitutedPattern = substitutedPattern.Replace($"#{i}", callParams[i].AsIC10);
                    }
                    if (method is MethodInfo methodInfo1 && methodInfo1.ReturnType != typeof(void))
                    {
                        int regIdx = AllocateRegister();
                        var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                        substitutedPattern = substitutedPattern.Replace("$", regValue.AsIC10);
                        valueStack.Push(regValue);
                    }
                    outputWriter.SetCode(instructionIndex, substitutedPattern);
                }
                else if (compileHintCallType == CompileHintCallType.CallStack)
                {
                    throw new NotImplementedException();
                }
                else
                {
                    throw new NotImplementedException();
                }
                CheckFree(callParams);
                return;
            }
            if (method.Name.StartsWith("set_"))
            {
                var valueToWrite = callParams[0];
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
                CheckFree(callParams);
                return;
            }
            if (method.Name.StartsWith("get_"))
            {
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
            if (method.DeclaringType == typeof(IC10Helpers) && method.Name == "GetTypeHash")
            {
                var deviceType = method.GetGenericArguments()[0];
                if (deviceType == null) { throw new InvalidOperationException(); }
                var deviceInterfaceAttr = deviceType.GetCustomAttribute<DeviceInterfaceAttribute>();
                if (deviceInterfaceAttr == null) { throw new InvalidOperationException($"GetTypeHash must be called with a valid device interface"); }
                valueStack.Push(new StaticStackValue { Value = deviceInterfaceAttr.TypeHash });
                CheckFree(callParams);
                return;
            }
            if (method is MethodInfo methodInfo)
            {
                if (callTarget is ThisStackValue || callTarget == null)
                {
                    ConstructCallSite(callParams, method, instructionIndex, outputWriter);
                    return;
                }
                else if (callTarget is DeviceStackValue deviceStackValue && methodInfo.Name.StartsWith("Get"))
                {
                    if (!deviceStackValue.Multicast) { throw new InvalidOperationException($"Tried to do multicast device read on non-multicast device pin"); }
                    string propertyName = method.Name[3..];
                    var regIdx = AllocateRegister();
                    var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                    int typeHash = deviceStackValue.DeviceType.GetCustomAttribute<DeviceInterfaceAttribute>()?.TypeHash ?? 0;
                    outputWriter.SetCode(instructionIndex, $"lb {regValue.AsIC10} {typeHash} {callTarget.AsIC10} {propertyName} {callParams[0].AsIC10}");
                    valueStack.Push(regValue);
                    CheckFree(callParams);
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
            if (isInline)
            {
                if (methodInfo.ReturnType == typeof(void))
                {
                    outputWriter.SetCode(instructionIndex, $"j {outputWriter.LabelPrefix}_end");
                    return;
                }
                if (returnStackValue is not RegisterStackValue registerStackValue) { throw new InvalidOperationException(); }
                var retVal = valueStack.Pop();
                outputWriter.SetCode(instructionIndex, $"move {registerStackValue.AsIC10} {retVal.AsIC10}{Environment.NewLine}j {outputWriter.LabelPrefix}_end");
                CheckFree(retVal);
            }
            else
            {
                if (methodInfo.ReturnType == typeof(void))
                {
                    outputWriter.SetCode(instructionIndex, $"j ra");
                    return;
                }
                var retVal = valueStack.Pop();
                outputWriter.SetCode(instructionIndex, $"push {retVal.AsIC10}{Environment.NewLine}j ra");
                CheckFree(retVal);
            }
            
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

        private void ConstructCallSite(StackValue[] callParams, MethodBase method, int instructionIndex, OutputWriter outputWriter)
        {
            methodDependencies.Add(method);
            CheckFree(callParams);
            StackValue? returnParam = null;
            var preReturnRegisterAllocs = currentRegisterAllocations;
            if (method is MethodInfo methodInfo && methodInfo.ReturnType != typeof(void))
            {
                var regIdx = AllocateRegister();
                var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                valueStack.Push(regValue);
                returnParam = regValue;
            }
            callSites.Add(new CallSite(instructionIndex, method, callParams, returnParam, preReturnRegisterAllocs, valueStack.ToArray()));
        }

        public void FixupCallSites(IReadOnlyDictionary<MethodBase, (ExecutionContext, OutputWriter)> contexts, OutputWriter outputWriter)
        {
            foreach (var callSite in callSites)
            {
                var (methodContext, methodOutputWriter) = contexts[callSite.TargetMethod];
                FixupCallSite(callSite, methodContext, methodOutputWriter, outputWriter);
            }
        }

        private void FixupCallSite(in CallSite callSite, ExecutionContext methodContext, OutputWriter methodOutputWriter, OutputWriter outputWriter)
        {
            if (ShouldInline(callSite, methodContext, methodOutputWriter))
            {
                GenerateInlineCall(callSite, methodContext, outputWriter);
            }
            else
            {
                GenerateCallStackCall(callSite, methodContext, outputWriter);
            }
        }

        private bool ShouldInline(in CallSite callSite, ExecutionContext methodContext, OutputWriter methodOutputWriter)
        {
            int totalWouldAllocate = methodContext.allUsedRegisters.NumAllocated + callSite.RegistersState.NumAllocated;
            if (totalWouldAllocate >= RegisterAllocations.NumTotal) { return false; }
            int callStackInstructionCount = EstimateCallStackCallInstructionCount(callSite, methodContext);
            var inlineInstructionCount = methodOutputWriter.ToString().Split(Environment.NewLine).Length - 1;
            // TODO: If there's only a single call ever to the target method, inlining would always result in less instructions, so discard this check
            if (inlineInstructionCount > callStackInstructionCount) { return false; }
            return true;
        }

        private void GenerateCallStackCall(in CallSite callSite, ExecutionContext methodContext, OutputWriter outputWriter)
        {
            var conflictingRegisters = methodContext.allUsedRegisters & callSite.RegistersState & ~reservedRegisters;
            var sb = new StringBuilder();
            for (int i = 0; i < RegisterAllocations.NumTotal; ++i)
            {
                if (conflictingRegisters.IsAllocated(i))
                {
                    sb.AppendLine($"push r{i}");
                }
            }
            sb.AppendLine("push ra");
            for (int i = 0; i < callSite.CallParams.Length; ++i)
            {
                sb.AppendLine($"push {callSite.CallParams[i].AsIC10}");
            }
            sb.AppendLine($"jal {callSite.TargetMethod.Name}");
            sb.AppendLine("pop ra");
            for (int i = 0; i < RegisterAllocations.NumTotal; ++i)
            {
                if (conflictingRegisters.IsAllocated(RegisterAllocations.NumTotal - (i + 1)))
                {
                    sb.AppendLine($"pop r{RegisterAllocations.NumTotal - (i + 1)}");
                }
            }
            outputWriter.SetCode(callSite.InstructionIndex, sb.ToString().Trim());
        }

        private int EstimateCallStackCallInstructionCount(in CallSite callSite, ExecutionContext methodContext)
        {
            var conflictingRegisters = methodContext.allUsedRegisters & callSite.RegistersState & ~reservedRegisters;
            return conflictingRegisters.NumAllocated + callSite.CallParams.Length + 3;
        }

        private void GenerateInlineCall(in CallSite callSite, ExecutionContext methodContext, OutputWriter outputWriter)
        {
            // We can't rely on precompiled method code as it's designed to be called via the call stack, so recompile it inline
            var registers = reservedRegisters | callSite.RegistersState;
            if (callSite.ReturnParam is RegisterStackValue registerStackValue) { registers.Allocate(registerStackValue.RegisterIndex); }
            var inlineContext = new ExecutionContext(registers, callSite.TargetMethod, true, callSite.CallParams, callSite.ReturnParam);
            var instructions = ILView.ToOpCodes(callSite.TargetMethod).ToArray();
            var inlineOutputWriter = new OutputWriter(instructions.Length);
            inlineOutputWriter.LabelPrefix = $"{outputWriter.GetLabel(callSite.InstructionIndex)}_inl";
            inlineContext.Compile(instructions, inlineOutputWriter);
            outputWriter.SetCode(callSite.InstructionIndex, inlineOutputWriter.ToString().Trim());
        }

        private StackValue[] GetCallParameters(MethodBase method)
        {
            var ps = method.GetParameters();
            var paramValues = new StackValue[ps.Length];
            for (int i = 0; i < ps.Length; ++i)
            {
                paramValues[ps.Length - (i + 1)] = valueStack.Pop();
            }
            return paramValues;
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

        private void CheckFree(IEnumerable<StackValue> stackValues)
        {
            foreach (var stackValue in stackValues)
            {
                CheckFree(stackValue);
            }
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
