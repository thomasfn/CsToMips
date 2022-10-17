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

    internal delegate void InstructionHandler(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter);

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
        private readonly int[] localVariableRegisters;
        private readonly StackValue?[] localVariableKnownStates;
        private RegisterAllocations currentRegisterAllocations;
        private RegisterAllocations allUsedRegisters;
        private readonly CompilerOptions compilerOptions;
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
            { typeof(Math).GetMethod("Clamp", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double), typeof(double), typeof(double) })!, "max %1 #1 #0\nmin $ #2 %1" },
            { typeof(Math).GetMethod("Clamp", BindingFlags.Public | BindingFlags.Static, new [] { typeof(float), typeof(float), typeof(float) })!, "max %1 #1 #0\nmin $ #2 %1" },
        };

        public IEnumerable<MethodBase> MethodDependencies => methodDependencies;

        public ExecutionContext(CompilerOptions compilerOptions, RegisterAllocations reservedRegisters, MethodBase method, bool isInline = false, IEnumerable<StackValue>? initialStackState = null, StackValue? returnStackValue = null)
        {
            instructionHandlers = typeof(ExecutionContext)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => (m, m.GetCustomAttribute<OpCodeHandlerAttribute>()))
                .Where(t => t.Item2 != null)
                .Select(t => new InstructionHandlerDefinition(t.Item2.OpCodeNameMatcher, t.m.CreateDelegate<InstructionHandler>(this)))
                .ToArray();
            valueStack = new Stack<StackValue>(initialStackState ?? Enumerable.Empty<StackValue>());
            var methodBody = method.GetMethodBody();
            if (methodBody == null) { throw new InvalidOperationException(); }
            this.compilerOptions = compilerOptions;
            this.reservedRegisters = reservedRegisters;
            this.method = method;
            currentRegisterAllocations = reservedRegisters;
            methodDependencies = new HashSet<MethodBase>();
            callSites = new List<CallSite>();
            var ps = method.GetParameters();
            paramIndexToStackValue = new StackValue[ps.Length];
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
            localVariableRegisters = new int[methodBody.LocalVariables.Count];
            localVariableKnownStates = new StackValue?[methodBody.LocalVariables.Count];
            for (int i = 0; i < localVariableRegisters.Length; ++i)
            {
                int width = GetTypeWidth(methodBody.LocalVariables[i].LocalType);
                if (width == 0)
                {
                    localVariableRegisters[i] = -1;
                    continue;
                }
                if (width > 1) { throw new NotImplementedException(); }
                localVariableRegisters[i] = AllocateRegister();
            }
            this.isInline = isInline;
            this.returnStackValue = returnStackValue;
        }

        private static int GetTypeWidth(Type t)
        {
            if (!t.IsValueType) { return 0; }
            if (t.IsConstructedGenericType && t.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>)) { return 0; }
            if (t.IsPrimitive || t.IsEnum) { return 1; }
            throw new NotImplementedException();
        }

        public void Compile(ReadOnlySpan<Instruction> instructions, OutputWriter outputWriter)
        {
            var preamble = new StringBuilder();
            if (isInline)
            {
                outputWriter.Postamble = $"{outputWriter.LabelPrefix}_end:";
            }
            else
            {
                for (int i = 0; i < paramIndexToStackValue.Length; ++i)
                {
                    preamble.AppendLine($"pop {paramIndexToStackValue[i].AsIC10}");
                }
                
            }
            var methodBody = method.GetMethodBody();
            if (methodBody?.InitLocals ?? false)
            {
                foreach (var localVar in methodBody.LocalVariables)
                {
                    if (!localVar.LocalType.IsValueType) { continue; }
                    
                }
            }
            outputWriter.Preamble = preamble.ToString().Trim();
            for (int i = 0; i < instructions.Length; ++i)
            {
                if (ProcessInstruction(instructions, ref i, outputWriter) == 0)
                {
                    throw new InvalidOperationException($"Unhandled instruction {instructions[i]}");
                }
            }
        }

        private int ProcessInstruction(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            int numHandlers = 0;
            foreach (var handlerDefinition in instructionHandlers)
            {
                if (handlerDefinition.OpCodeNameMatcher.IsMatch(instruction.OpCode.Name ?? ""))
                {
                    handlerDefinition.Handler(instructions, ref instructionIndex, outputWriter);
                    ++numHandlers;
                }
            }
            return numHandlers;
        }

        [OpCodeHandler(@"nop")]
        private void Handle_Noop(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter) { }

        [OpCodeHandler(@"dup")]
        private void Handle_Dup(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var value = valueStack.Peek();
            valueStack.Push(value);
        }

        [OpCodeHandler(@"pop")]
        private void Handle_Pop(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            valueStack.Pop();
        }

        [OpCodeHandler(@"ldarg(\.[0-9])?")]
        private void Handle_Ldarg(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
        private void Handle_Ldstr(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            valueStack.Push(new StringStackValue { Value = (string)instruction.Data });
        }

        [OpCodeHandler(@"ldnull")]
        private void Handle_Ldnull(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            valueStack.Push(new NullStackValue());
        }

        [OpCodeHandler(@"ldind\.[a-z]+[0-9]*")]
        private void Handle_Ldind(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var value = valueStack.Pop();
            if (instruction.OpCode == OpCodes.Ldind_Ref && value is DeviceSlotStackValue deviceSlotStackValue)
            {
                valueStack.Push(value);
                return;
            }
            throw new InvalidOperationException();
        }

        [OpCodeHandler(@"ldfld")]
        private void Handle_Ldfld(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
        private void Handle_Stfld(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
        private void Handle_Call(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
                    var tempRegisterMap = new int?[RegisterAllocations.NumTotal];
                    substitutedPattern = new Regex(@"%[0-9]{1,2}").Replace(substitutedPattern, (match) =>
                    {
                        var tempRegisterIdx = int.Parse(match.Value[1..]);
                        if (tempRegisterMap[tempRegisterIdx] == null)
                        {
                            tempRegisterMap[tempRegisterIdx] = AllocateRegister();
                        }
                        return $"r{tempRegisterMap[tempRegisterIdx]}";
                    });
                    if (method is MethodInfo methodInfo1 && methodInfo1.ReturnType != typeof(void))
                    {
                        int regIdx = AllocateRegister();
                        var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                        substitutedPattern = substitutedPattern.Replace("$", regValue.AsIC10);
                        valueStack.Push(regValue);
                    }
                    foreach (var tempReg in tempRegisterMap)
                    {
                        if (tempReg == null) { continue; }
                        CheckFree(new RegisterStackValue { RegisterIndex = tempReg.Value });
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
                    throw new InvalidOperationException($"Can't call methods on unsupported value '{callTarget}'");
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
                if (callTarget is DeviceSlotsStackValue deviceSlotsStackValue)
                {
                    if (method.Name == "get_Item")
                    {
                        valueStack.Push(new DeviceSlotStackValue { DeviceType = deviceSlotsStackValue.DeviceType, PinName = deviceSlotsStackValue.PinName, SlotIndex = callParams[0] });
                        return;
                    }
                    if (method.Name == "get_Length")
                    {
                        valueStack.Push(new StaticStackValue { Value = deviceSlotsStackValue.DeviceType.GetProperty("Slots", BindingFlags.Public | BindingFlags.Instance)?.GetCustomAttribute<DeviceSlotCountAttribute>()?.SlotCount ?? 0 });
                        return;
                    }
                }
                if (callTarget is DeviceSlotStackValue deviceSlotStackValue)
                {
                    string propertyName = method.Name[4..];
                    var regIdx = AllocateRegister();
                    var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                    outputWriter.SetCode(instructionIndex, $"ls {regValue.AsIC10} {deviceSlotStackValue.PinName} {deviceSlotStackValue.SlotIndex.AsIC10} {propertyName}");
                    CheckFree(deviceSlotStackValue.SlotIndex);
                    valueStack.Push(regValue);
                    return;
                }
                if (callTarget is DeviceStackValue deviceStackValue)
                {
                    if (deviceStackValue.Multicast) { throw new InvalidOperationException($"Tried to do non-multicast device read on multicast device pin"); }
                    if (method.Name == "get_Slots")
                    {
                        valueStack.Push(new DeviceSlotsStackValue { DeviceType = deviceStackValue.DeviceType, PinName = deviceStackValue.PinName });
                        return;
                    }
                    string propertyName = method.Name[4..];
                    var regIdx = AllocateRegister();
                    var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                    outputWriter.SetCode(instructionIndex, $"l {regValue.AsIC10} {callTarget.AsIC10} {propertyName}");
                    valueStack.Push(regValue);
                    return;
                }
                throw new InvalidOperationException($"Can't call methods on unsupported value '{callTarget}'");
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
        private void Handle_Branch(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
                if (lhs is DeviceStackValue deviceStackValue)
                {
                    if (instruction.OpCode == OpCodes.Brtrue || instruction.OpCode == OpCodes.Brtrue_S) { cmd1Arg = "bdse"; }
                    else if (instruction.OpCode == OpCodes.Brfalse || instruction.OpCode == OpCodes.Brfalse_S) { cmd1Arg = "bdns"; }
                    else { throw new InvalidOperationException(); }
                }
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

        [OpCodeHandler(@"ldc(\.[ir][0-9](\.(m)?[0-9])?)?(\.s)?")]
        private void Handle_Ldc(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
            else if (instruction.OpCode == OpCodes.Ldc_I4_S) { valueStack.Push(new StaticStackValue { Value = (byte)instruction.Data }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_M1) { valueStack.Push(new StaticStackValue { Value = -1 }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_R4) { valueStack.Push(new StaticStackValue { Value = (float)instruction.Data }); return; }
            else if (instruction.OpCode == OpCodes.Ldc_R8) { valueStack.Push(new StaticStackValue { Value = (float)(double)instruction.Data }); return; }
            throw new InvalidOperationException();
        }

        [OpCodeHandler(@"stloc(\.[0-9]|\.s)?")]
        private void Handle_Stloc(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
            var value = valueStack.Pop();
            localVariableKnownStates[localVariableIndex] = value;
            if (localVariableRegisters[localVariableIndex] != -1)
            {
                outputWriter.SetCode(instructionIndex, $"move r{localVariableRegisters[localVariableIndex]} {value.AsIC10}");
            }
            CheckFree(value);
        }

        [OpCodeHandler(@"ldloc(\.[0-9]|\.s)?")]
        private void Handle_Ldloc(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
            valueStack.Push(new RegisterStackValue { RegisterIndex = localVariableRegisters[localVariableIndex] });
        }

        [OpCodeHandler(@"ldloca(\.s)?")]
        private void Handle_Ldloca(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            int localVariableIndex;
            if (instruction.OpCode == OpCodes.Ldloca) { localVariableIndex = (int)instruction.Data; }
            else if (instruction.OpCode == OpCodes.Ldloca_S) { localVariableIndex = (sbyte)instruction.Data; }
            else { throw new InvalidOperationException(); }
            if (localVariableKnownStates[localVariableIndex] is DeviceSlotsStackValue deviceSlotsStackValue)
            {
                valueStack.Push(deviceSlotsStackValue);
                return;
            }

            throw new NotImplementedException();
        }

        [OpCodeHandler(@"add|sub|mul|div|and|or|xor")]
        private void Handle_BinaryArithmetic(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var rhs = valueStack.Pop();
            var lhs = valueStack.Pop();
            if (lhs is StaticStackValue lhsStatic && rhs is StaticStackValue rhsStatic)
            {
                float result;
                if (instruction.OpCode == OpCodes.Add) { result = lhsStatic.Value + rhsStatic.Value; }
                else if (instruction.OpCode == OpCodes.Sub) { result = lhsStatic.Value - rhsStatic.Value; }
                else if (instruction.OpCode == OpCodes.Mul) { result = lhsStatic.Value * rhsStatic.Value; }
                else if (instruction.OpCode == OpCodes.Div) { result = lhsStatic.Value / rhsStatic.Value; }
                else if (instruction.OpCode == OpCodes.And) { result = (lhsStatic.Value != 0.0f && rhsStatic.Value != 0.0f) ? 1.0f : 0.0f; }
                else if (instruction.OpCode == OpCodes.Or) { result = (lhsStatic.Value != 0.0f || rhsStatic.Value != 0.0f) ? 1.0f : 0.0f; }
                else if (instruction.OpCode == OpCodes.Xor) { result = (int)lhsStatic.Value ^ (int)rhsStatic.Value; }
                else { throw new InvalidOperationException(); }
                valueStack.Push(new StaticStackValue { Value = result });
                return;
            }
            string ic10;
            if (instruction.OpCode == OpCodes.Add) { ic10 = "add"; }
            else if (instruction.OpCode == OpCodes.Sub) { ic10 = "sub"; }
            else if (instruction.OpCode == OpCodes.Mul) { ic10 = "mul"; }
            else if (instruction.OpCode == OpCodes.Div) { ic10 = "div"; }
            else if (instruction.OpCode == OpCodes.And) { ic10 = "and"; }
            else if (instruction.OpCode == OpCodes.Or) { ic10 = "or"; }
            else if (instruction.OpCode == OpCodes.Xor) { ic10 = "xor"; }
            else { throw new InvalidOperationException(); }
            EmitWriteInstruction($"{ic10} $ {lhs.AsIC10} {rhs.AsIC10}", instructions, ref instructionIndex, outputWriter);
            CheckFree(rhs);
            CheckFree(lhs);
        }

        [OpCodeHandler(@"not")]
        private void Handle_UnaryArithmetic(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var rhs = valueStack.Pop();
            string ic10;
            if (instruction.OpCode == OpCodes.Not) { ic10 = "not"; }
            else { throw new InvalidOperationException(); }
            EmitWriteInstruction($"{ic10} $ {rhs.AsIC10}", instructions, ref instructionIndex, outputWriter);
            CheckFree(rhs);
        }

        [OpCodeHandler(@"neg")]
        private void Handle_Neg(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var value = valueStack.Pop();
            EmitWriteInstruction($"sub $ 0 {value.AsIC10}", instructions, ref instructionIndex, outputWriter);
            CheckFree(value);
        }

        [OpCodeHandler(@"(ceq|cgt|clt)(\.un)?")]
        private void Handle_Compare(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            var instruction = instructions[instructionIndex];
            var rhs = valueStack.Pop();
            var lhs = valueStack.Pop();
            if (((lhs is DeviceStackValue && rhs is NullStackValue) || (rhs is DeviceStackValue && lhs is NullStackValue)) && instruction.OpCode == OpCodes.Cgt_Un)
            {
                var deviceStackValue = (lhs as DeviceStackValue) ?? (rhs as DeviceStackValue);
                if (deviceStackValue == null) { throw new InvalidOperationException(); }
                EmitWriteInstruction($"sdse $ {deviceStackValue.AsIC10}", instructions, ref instructionIndex, outputWriter);
                CheckFree(rhs);
                CheckFree(lhs);
                return;
            }
            string ic10;
            if (instruction.OpCode == OpCodes.Ceq) { ic10 = "seq"; }
            else if (instruction.OpCode == OpCodes.Cgt) { ic10 = "sgt"; }
            else if (instruction.OpCode == OpCodes.Cgt_Un) { ic10 = "sgt"; }
            else if (instruction.OpCode == OpCodes.Clt) { ic10 = "slt"; }
            else if (instruction.OpCode == OpCodes.Clt_Un) { ic10 = "slt"; }
            else { throw new InvalidOperationException(); }
            EmitWriteInstruction($"{ic10} $ {lhs.AsIC10} {rhs.AsIC10}", instructions, ref instructionIndex, outputWriter);
            CheckFree(rhs);
            CheckFree(lhs);
        }

        [OpCodeHandler(@"switch")]
        private void Handle_Switch(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
        private void Handle_Ret(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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
        private void Handle_Conv(ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
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

        private void EmitWriteInstruction(string pattern, ReadOnlySpan<Instruction> instructions, ref int instructionIndex, OutputWriter outputWriter)
        {
            // A common pattern is some IL that pops multiple values off the stack, pushes a single value to the stack, then uses stloc to store in a local variable
            // Following that the usual way results in an intermediate register being allocated for the single value, followed by a move to shift it to the local variable's register
            // This could be optimised out by having the instruction store directly to the local variable, skipping the need for an intermediate register
            // This sort of optimisation COULD be handled by the optimiser when it gets upgraded to understand flow and register usage spans, but for now we can handle it by peeking ahead for a stloc
            if (instructionIndex < instructions.Length - 1)
            {
                var nextInstruction = instructions[instructionIndex + 1];
                int? localVariableIndex = null;
                if (nextInstruction.OpCode == OpCodes.Stloc) { localVariableIndex = (int)nextInstruction.Data; }
                else if (nextInstruction.OpCode == OpCodes.Stloc_0) { localVariableIndex = 0; }
                else if (nextInstruction.OpCode == OpCodes.Stloc_1) { localVariableIndex = 1; }
                else if (nextInstruction.OpCode == OpCodes.Stloc_2) { localVariableIndex = 2; }
                else if (nextInstruction.OpCode == OpCodes.Stloc_3) { localVariableIndex = 3; }
                else if (nextInstruction.OpCode == OpCodes.Stloc_S) { localVariableIndex = (sbyte)nextInstruction.Data; }
                if (localVariableIndex != null)
                {
                    var localVarRegIdx = localVariableRegisters[localVariableIndex.Value];
                    if (localVarRegIdx != -1)
                    {
                        outputWriter.SetCode(instructionIndex, pattern.Replace("$", $"r{localVarRegIdx}"));
                        ++instructionIndex;
                        return;
                    }
                }
            }
            var regIdx = AllocateRegister();
            var regValue = new RegisterStackValue { RegisterIndex = regIdx };
            outputWriter.SetCode(instructionIndex, pattern.Replace("$", regValue.AsIC10));
            valueStack.Push(regValue);
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
                FixupCallSite(contexts, callSite, methodContext, methodOutputWriter, outputWriter);
            }
        }

        private void FixupCallSite(IReadOnlyDictionary<MethodBase, (ExecutionContext, OutputWriter)> contexts, in CallSite callSite, ExecutionContext methodContext, OutputWriter methodOutputWriter, OutputWriter outputWriter)
        {
            if (ShouldInline(callSite, methodContext, methodOutputWriter))
            {
                GenerateInlineCall(contexts, callSite, methodContext, outputWriter);
            }
            else
            {
                GenerateCallStackCall(contexts, callSite, methodContext, outputWriter);
            }
        }

        private bool ShouldInline(in CallSite callSite, ExecutionContext methodContext, OutputWriter methodOutputWriter)
        {
            if (!compilerOptions.AllowInlining) { return false; }
            int totalWouldAllocate = methodContext.allUsedRegisters.NumAllocated + callSite.RegistersState.NumAllocated;
            if (totalWouldAllocate >= RegisterAllocations.NumTotal) { return false; }
            int callStackInstructionCount = EstimateCallStackCallInstructionCount(callSite, methodContext);
            var inlineInstructionCount = methodOutputWriter.ToString().Split(Environment.NewLine).Length - 1;
            // TODO: If there's only a single call ever to the target method, inlining would always result in less instructions, so discard this check
            //if (inlineInstructionCount > callStackInstructionCount) { return false; }
            return true;
        }

        private void GenerateCallStackCall(IReadOnlyDictionary<MethodBase, (ExecutionContext, OutputWriter)> contexts, in CallSite callSite, ExecutionContext methodContext, OutputWriter outputWriter)
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

        private void GenerateInlineCall(IReadOnlyDictionary<MethodBase, (ExecutionContext, OutputWriter)> contexts, in CallSite callSite, ExecutionContext methodContext, OutputWriter outputWriter)
        {
            // We can't rely on precompiled method code as it's designed to be called via the call stack, so recompile it inline
            var registers = reservedRegisters | callSite.RegistersState;
            if (callSite.ReturnParam is RegisterStackValue registerStackValue) { registers.Allocate(registerStackValue.RegisterIndex); }
            var inlineContext = new ExecutionContext(compilerOptions, registers, callSite.TargetMethod, true, callSite.CallParams.Reverse(), callSite.ReturnParam);
            var instructions = ILView.ToOpCodes(callSite.TargetMethod).ToArray();
            var inlineOutputWriter = new OutputWriter(instructions.Length);
            inlineOutputWriter.LabelPrefix = $"{outputWriter.GetLabel(callSite.InstructionIndex)}_inl";
            inlineContext.Compile(instructions, inlineOutputWriter);
            inlineContext.FixupCallSites(contexts, inlineOutputWriter);
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
            foreach (var localRegisterIndex in localVariableRegisters)
            {
                if (localRegisterIndex == registerIndex) { ++refCount; }
            }
            return refCount;
        }
    }
}
