using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Immutable;

namespace CsToMips.Compiler
{
    using Devices;
    using System.Runtime.CompilerServices;

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

    internal struct ExecutionState
    {
        public VirtualStack VirtualStack;
        public RegisterAllocations RegisterAllocations;
        public ImmutableArray<CallSite> CallSites;
        public ImmutableArray<int> LocalVariableRegisterMappings;
        public ImmutableArray<StackValue?> LocalVariableKnownStates;
        public ImmutableArray<FragmentText> PendingIntermediateFragments;
    }

    internal readonly struct FragmentText
    {
        public readonly string Code;
        public readonly ImmutableArray<int> BranchTargets;

        public FragmentText(string code, ImmutableArray<int>? branchTargets = null)
        {
            Code = code;
            BranchTargets = branchTargets ?? ImmutableArray<int>.Empty;
        }

        public static readonly FragmentText Empty = new(string.Empty, null);

        public FragmentText Replace(string oldValue, string newValue)
            => new FragmentText(Code.Replace(oldValue, newValue), BranchTargets);

        public static FragmentText operator +(in FragmentText lhs, in FragmentText rhs)
            => new FragmentText($"{lhs.Code}{Environment.NewLine}{rhs.Code}".Trim(), lhs.BranchTargets.AddRange(rhs.BranchTargets));
    }

    internal readonly struct Fragment
    {
        public readonly int SourceInstructionIndex;
        public readonly ExecutionState PreExecutionState;
        public readonly ExecutionState PostExecutionState;
        public readonly FragmentText Text;

        public Fragment(int sourceInstructionIndex, ExecutionState preExecutionState, ExecutionState postExecutionState, FragmentText text)
        {
            SourceInstructionIndex = sourceInstructionIndex;
            PreExecutionState = preExecutionState;
            PostExecutionState = postExecutionState;
            Text = text;
        }
    }

    internal delegate FragmentText InstructionHandler(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState);

    internal class ExecutionContext
    {
        private readonly IEnumerable<InstructionHandlerDefinition> instructionHandlers;
        
        private readonly ExecutionState initialExecutionState;
        private readonly CompilerOptions compilerOptions;
        private readonly RegisterAllocations reservedRegisters;
        private readonly MethodBase method;
        private readonly ISet<MethodBase> methodDependencies;
        private readonly StackValue[] paramIndexToStackValue;
        private StackValue? returnStackValue;
        private readonly bool isInline;
        private string localLabelPrefix = string.Empty;

        private RegisterAllocations allUsedRegisters;

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

            { typeof(float).GetMethod("IsNaN", BindingFlags.Public | BindingFlags.Static, new [] { typeof(float) })!, "snan $ #0" },
            { typeof(double).GetMethod("IsNaN", BindingFlags.Public | BindingFlags.Static, new [] { typeof(double) })!, "snan $ #0" },
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
            initialExecutionState.VirtualStack = VirtualStack.FromEnumerable(initialStackState ?? Enumerable.Empty<StackValue>());
            var methodBody = method.GetMethodBody();
            if (methodBody == null) { throw new InvalidOperationException(); }
            this.compilerOptions = compilerOptions;
            this.reservedRegisters = reservedRegisters;
            this.method = method;
            initialExecutionState.RegisterAllocations = reservedRegisters;
            methodDependencies = new HashSet<MethodBase>();
            var ps = method.GetParameters();
            paramIndexToStackValue = new StackValue[ps.Length];
            if (isInline)
            {
                for (int i = 0; i < ps.Length; ++i)
                {
                    initialExecutionState.VirtualStack = initialExecutionState.VirtualStack.Pop(out paramIndexToStackValue[i]);
                    if (paramIndexToStackValue[i] is RegisterStackValue registerStackValue) { initialExecutionState.RegisterAllocations = initialExecutionState.RegisterAllocations.Allocate(registerStackValue.RegisterIndex); }
                }
            }
            else
            {
                for (int i = 0; i < ps.Length; ++i)
                {
                    paramIndexToStackValue[i] = new RegisterStackValue { RegisterIndex = AllocateRegister(ref initialExecutionState) };
                }
            }
            var localVariableRegisters = new int[methodBody.LocalVariables.Count];
            var localVariableKnownStates = new StackValue?[methodBody.LocalVariables.Count];
            for (int i = 0; i < localVariableRegisters.Length; ++i)
            {
                int width = GetTypeWidth(methodBody.LocalVariables[i].LocalType);
                if (width == 0)
                {
                    localVariableRegisters[i] = -1;
                    continue;
                }
                if (width > 1) { throw new NotImplementedException(); }
                localVariableRegisters[i] = AllocateRegister(ref initialExecutionState);
            }
            initialExecutionState.LocalVariableRegisterMappings = localVariableRegisters.ToImmutableArray();
            initialExecutionState.LocalVariableKnownStates = localVariableKnownStates.ToImmutableArray();
            initialExecutionState.PendingIntermediateFragments = ImmutableArray<FragmentText>.Empty;
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
            localLabelPrefix = outputWriter.LabelPrefix;
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
            var executionState = initialExecutionState;
            var fragmentList = new Fragment[instructions.Length];
            for (int i = 0; i < instructions.Length; ++i)
            {
                var fragment = ProcessInstruction(instructions, i, executionState);
                if (fragment == null)
                {
                    throw new InvalidOperationException($"Unhandled instruction {instructions[i]}");
                }
                fragmentList[i] = fragment.Value;
                outputWriter.SetCode(i, fragment.Value.Text.Code);
                executionState = fragment.Value.PostExecutionState;
                foreach (var branchTarget in fragment.Value.Text.BranchTargets)
                {
                    outputWriter.SetWithLabel(branchTarget, true);
                }
            }

            // Check that all branches have stack and register allocation consistency
            for (int i = 0; i < instructions.Length; ++i)
            {
                ref var fragment = ref fragmentList[i];
                foreach (var branchTarget in fragment.Text.BranchTargets)
                {
                    ref var branchFragment = ref fragmentList[branchTarget];
                    if (fragment.PostExecutionState.VirtualStack != branchFragment.PreExecutionState.VirtualStack)
                    {
                        throw new NotImplementedException($"Branch stack inconsistency");
                    }
                    if (fragment.PostExecutionState.RegisterAllocations != branchFragment.PreExecutionState.RegisterAllocations)
                    {
                        throw new NotImplementedException($"Branch register allocation inconsistency");
                    }
                    for (int j = 0; j < fragment.PostExecutionState.LocalVariableKnownStates.Length; ++j)
                    {
                        var preBranchVarKnownState = fragment.PostExecutionState.LocalVariableKnownStates[j];
                        var postBranchVarKnownState = branchFragment.PreExecutionState.LocalVariableKnownStates[j];
                        if (postBranchVarKnownState == null)
                        {
                            // The code that we're jumping to makes no assumptions about the state of the local variable
                            // So, the current known state of the local variable is irrelevant
                            continue;
                        }
                        if (preBranchVarKnownState != postBranchVarKnownState)
                        {
                            // The code that we're jumping to is making an assumption about the state of the local variable
                            // Furthermore this conflicts with our current known state of the local variable
                            //throw new NotImplementedException($"Branch local variable known state inconsistency");
                        }
                    }
                }
            }
        }

        private Fragment? ProcessInstruction(ReadOnlySpan<Instruction> instructions, int instructionIndex, in ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            foreach (var handlerDefinition in instructionHandlers)
            {
                if (handlerDefinition.OpCodeNameMatcher.IsMatch(instruction.OpCode.Name ?? ""))
                {
                    var postExecutionState = executionState;
                    var fragmentText = handlerDefinition.Handler(instructions, instructionIndex, ref postExecutionState);
                    FragmentText preFragmentText = FragmentText.Empty;
                    foreach (var intermediateFragment in postExecutionState.PendingIntermediateFragments)
                    {
                        preFragmentText += intermediateFragment;
                    }
                    postExecutionState.PendingIntermediateFragments = ImmutableArray<FragmentText>.Empty;
                    return new Fragment(instructionIndex, executionState, postExecutionState, preFragmentText + fragmentText);
                }
            }
            return null;
        }

        [OpCodeHandler(@"nop")]
        private FragmentText Handle_Noop(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
            => FragmentText.Empty;

        [OpCodeHandler(@"dup")]
        private FragmentText Handle_Dup(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var value = executionState.VirtualStack.Peek();
            if (value is DeferredExpressionStackValue)
            {
                executionState.VirtualStack = executionState.VirtualStack.Pop(out value);
                ResolveInputValue(value, ref executionState);
                executionState.VirtualStack = executionState.VirtualStack.Push(value);
            }
            executionState.VirtualStack = executionState.VirtualStack.Push(value);
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"pop")]
        private FragmentText Handle_Pop(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            executionState.VirtualStack = executionState.VirtualStack.Pop(out _);
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"ldarg(\.[0-9])?")]
        private FragmentText Handle_Ldarg(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            if (instruction.OpCode == OpCodes.Ldarg_0 || (instruction.OpCode == OpCodes.Ldarg && (int)instruction.Data == 0))
            {
                executionState.VirtualStack = executionState.VirtualStack.Push(new ThisStackValue());
            }
            else if (instruction.OpCode == OpCodes.Ldarg_1) { executionState.VirtualStack = executionState.VirtualStack.Push(paramIndexToStackValue[0]); }
            else if (instruction.OpCode == OpCodes.Ldarg_2) { executionState.VirtualStack = executionState.VirtualStack.Push(paramIndexToStackValue[1]); }
            else if (instruction.OpCode == OpCodes.Ldarg_3) { executionState.VirtualStack = executionState.VirtualStack.Push(paramIndexToStackValue[2]); }
            else if (instruction.OpCode == OpCodes.Ldarg_S) { executionState.VirtualStack = executionState.VirtualStack.Push(paramIndexToStackValue[(sbyte)instruction.Data]); }
            else throw new InvalidOperationException();
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"ldstr")]
        private FragmentText Handle_Ldstr(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Push(new StringStackValue { Value = (string)instruction.Data });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"ldnull")]
        private FragmentText Handle_Ldnull(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            executionState.VirtualStack = executionState.VirtualStack.Push(new NullStackValue());
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"ldind\.[a-z]+[0-9]*")]
        private FragmentText Handle_Ldind(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop(out var value);
            if (instruction.OpCode == OpCodes.Ldind_Ref && value is DeviceSlotStackValue deviceSlotStackValue)
            {
                executionState.VirtualStack = executionState.VirtualStack.Push(value);
                return FragmentText.Empty;
            }
            throw new InvalidOperationException();
        }

        [OpCodeHandler(@"ldfld")]
        private FragmentText Handle_Ldfld(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop(out var value);
            var fieldInfo = instruction.Data as FieldInfo;
            if (value is not ThisStackValue || fieldInfo == null)
            {
                throw new InvalidOperationException($"Can't read fields or properties on unsupported value '${value}'");
            }
            var deviceAttr = fieldInfo.GetCustomAttribute<DeviceAttribute>();
            if (deviceAttr != null)
            {

                executionState.VirtualStack = executionState.VirtualStack.Push(new DeviceStackValue { PinName = deviceAttr.PinName, DeviceType = fieldInfo.FieldType, Multicast = false });
                return FragmentText.Empty;
            }
            var multicastDeviceAttr = fieldInfo.GetCustomAttribute<MulticastDeviceAttribute>();
            if (multicastDeviceAttr != null)
            {
                executionState.VirtualStack = executionState.VirtualStack.Push(new DeviceStackValue { PinName = "", DeviceType = fieldInfo.FieldType, Multicast = true });
                return FragmentText.Empty;
            }
            executionState.VirtualStack = executionState.VirtualStack.Push(new FieldStackValue { UnderlyingField = fieldInfo, AliasName = fieldInfo.Name });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"stfld")]
        private FragmentText Handle_Stfld(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop2(out var value, out var target);
            value = ResolveInputValue(value, ref executionState);
            var fieldInfo = instruction.Data as FieldInfo;
            if (target is not ThisStackValue || fieldInfo == null)
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
            return new FragmentText($"move {fieldInfo.Name} {value.AsIC10}");
        }

        [OpCodeHandler(@"call(virt)?")]
        private FragmentText Handle_Call(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            var method = instruction.Data as MethodBase;
            if (method == null || method.IsConstructor)
            {
                // This could be a super call on the ctor, for now let's ignore
                return FragmentText.Empty;
            }
            var callParams = GetCallParameters(ref executionState, method);
            StackValue? callTarget = null;
            if (!method.IsStatic) { executionState.VirtualStack = executionState.VirtualStack.Pop(out callTarget); }
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
                string fragmentCode = string.Empty;
                if (compileHintCallType == CompileHintCallType.Inline)
                {
                    for (int i = 0; i < callParams.Length; ++i)
                    {
                        substitutedPattern = substitutedPattern.Replace($"#{i}", callParams[i].AsIC10);
                    }
                    var tempRegisterMap = new int?[RegisterAllocations.NumTotal];
                    var tmpExecutionState = executionState;
                    substitutedPattern = new Regex(@"%[0-9]{1,2}").Replace(substitutedPattern, (match) =>
                    {
                        var tempRegisterIdx = int.Parse(match.Value[1..]);
                        if (tempRegisterMap[tempRegisterIdx] == null)
                        {
                            tempRegisterMap[tempRegisterIdx] = AllocateRegister(ref tmpExecutionState);
                        }
                        return $"r{tempRegisterMap[tempRegisterIdx]}";
                    });
                    executionState = tmpExecutionState;
                    if (method is MethodInfo methodInfo1 && methodInfo1.ReturnType != typeof(void))
                    {
                        int regIdx = AllocateRegister(ref executionState);
                        var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                        substitutedPattern = substitutedPattern.Replace("$", regValue.AsIC10);
                        executionState.VirtualStack = executionState.VirtualStack.Push(regValue);
                    }
                    foreach (var tempReg in tempRegisterMap)
                    {
                        if (tempReg == null) { continue; }
                        CheckFree(ref executionState, new RegisterStackValue { RegisterIndex = tempReg.Value });
                    }
                    fragmentCode = substitutedPattern;
                }
                else if (compileHintCallType == CompileHintCallType.CallStack)
                {
                    throw new NotImplementedException();
                }
                else
                {
                    throw new NotImplementedException();
                }
                CheckFree(ref executionState, callParams);
                return new FragmentText(fragmentCode);
            }
            if (method.Name.StartsWith("set_"))
            {
                var valueToWrite = callParams[0];
                if (callTarget is not DeviceStackValue deviceStackValue)
                {
                    throw new InvalidOperationException($"Can't call methods on unsupported value '{callTarget}'");
                }
                string propertyName = method.Name[4..];
                string fragmentCode;
                if (deviceStackValue.Multicast)
                {
                    string? typeName = deviceStackValue.DeviceType.GetCustomAttribute<DeviceInterfaceAttribute>()?.TypeName;
                    fragmentCode = $"sb HASH(\"{typeName}\") {propertyName} {valueToWrite.AsIC10}";
                }
                else
                {
                    fragmentCode = $"s {callTarget.AsIC10} {propertyName} {valueToWrite.AsIC10}";
                }
                CheckFree(ref executionState, callParams);
                return new FragmentText(fragmentCode);
            }
            if (method.Name.StartsWith("get_"))
            {
                if (callTarget is DeviceSlotsStackValue deviceSlotsStackValue)
                {
                    if (method.Name == "get_Item")
                    {
                        executionState.VirtualStack = executionState.VirtualStack.Push(new DeviceSlotStackValue { DeviceType = deviceSlotsStackValue.DeviceType, PinName = deviceSlotsStackValue.PinName, SlotIndex = callParams[0] });
                        return FragmentText.Empty;
                    }
                    if (method.Name == "get_Length")
                    {
                        executionState.VirtualStack = executionState.VirtualStack.Push(new StaticStackValue { Value = deviceSlotsStackValue.DeviceType.GetProperty("Slots", BindingFlags.Public | BindingFlags.Instance)?.GetCustomAttribute<DeviceSlotCountAttribute>()?.SlotCount ?? 0 });
                        return FragmentText.Empty;
                    }
                }
                if (callTarget is DeviceSlotStackValue deviceSlotStackValue)
                {
                    string propertyName = method.Name[4..];
                    executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
                    {
                        ExpressionText = new FragmentText($"ls $ {deviceSlotStackValue.PinName} {deviceSlotStackValue.SlotIndex.AsIC10} {propertyName}"),
                        FreeValues = new StackValue[] { deviceSlotStackValue.SlotIndex }.ToImmutableArray(),
                    });
                    return FragmentText.Empty;
                }
                if (callTarget is DeviceStackValue deviceStackValue)
                {
                    if (deviceStackValue.Multicast) { throw new InvalidOperationException($"Tried to do non-multicast device read on multicast device pin"); }
                    if (method.Name == "get_Slots")
                    {
                        executionState.VirtualStack = executionState.VirtualStack.Push(new DeviceSlotsStackValue { DeviceType = deviceStackValue.DeviceType, PinName = deviceStackValue.PinName });
                        return FragmentText.Empty;
                    }
                    string propertyName = method.Name[4..];
                    executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
                    {
                        ExpressionText = new FragmentText($"l $ {callTarget.AsIC10} {propertyName}"),
                        FreeValues = ImmutableArray<StackValue>.Empty,
                    });
                    return FragmentText.Empty;
                }
                throw new InvalidOperationException($"Can't call methods on unsupported value '{callTarget}'");
            }
            if (method.DeclaringType == typeof(IC10Helpers) && method.Name == "GetTypeHash")
            {
                var deviceType = method.GetGenericArguments()[0];
                if (deviceType == null) { throw new InvalidOperationException(); }
                var deviceInterfaceAttr = deviceType.GetCustomAttribute<DeviceInterfaceAttribute>();
                if (deviceInterfaceAttr == null) { throw new InvalidOperationException($"GetTypeHash must be called with a valid device interface"); }
                executionState.VirtualStack = executionState.VirtualStack.Push(new HashStringStackValue { Value = deviceInterfaceAttr.TypeName });
                CheckFree(ref executionState, callParams);
                return FragmentText.Empty;
            }
            if (method.DeclaringType == typeof(IC10Helpers) && method.Name == "Hash")
            {
                if (callParams[0] is not StringStackValue stringStackValue) { throw new InvalidOperationException(); }
                executionState.VirtualStack = executionState.VirtualStack.Push(new HashStringStackValue { Value = stringStackValue.Value });
                CheckFree(ref executionState, callParams);
                return FragmentText.Empty;
            }
            if (method is MethodInfo methodInfo)
            {
                if (callTarget is ThisStackValue || callTarget == null)
                {
                    var callSiteText = GenerateCallSite($"{GetInstructionLabel(instructionIndex)}_inl", methodInfo, callParams, ref executionState);
                    CheckFree(ref executionState, callParams);
                    return callSiteText;
                }
                else if (callTarget is DeviceStackValue deviceStackValue && methodInfo.Name.StartsWith("Get"))
                {
                    if (!deviceStackValue.Multicast) { throw new InvalidOperationException($"Tried to do multicast device read on non-multicast device pin"); }
                    string propertyName = method.Name[3..];
                    var regIdx = AllocateRegister(ref executionState);
                    var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                    string? typeName = deviceStackValue.DeviceType.GetCustomAttribute<DeviceInterfaceAttribute>()?.TypeName;
                    var code = $"lb {regValue.AsIC10} HASH(\"{typeName}\") {callTarget.AsIC10} {propertyName} {callParams[0].AsIC10}";
                    executionState.VirtualStack = executionState.VirtualStack.Push(regValue);
                    CheckFree(ref executionState, callParams);
                    return new FragmentText(code);
                }
                throw new InvalidOperationException($"Can't call unsupported method '{method}'");
            }

            throw new InvalidOperationException($"Can't call unsupported method '{method}'");
        }

        [OpCodeHandler(@"b(r|eq|ge|gt|le|lt|ne|rfalse|rtrue|rzero)(\.un)?(\.s)?")]
        private FragmentText Handle_Branch(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            int offset;
            if (instruction.Data is int intData)
            {
                offset = intData;
            }
            else if (instruction.Data is sbyte sbyteData)
            {
                offset = sbyteData;
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
                executionState.VirtualStack = executionState.VirtualStack.Pop2(out var rhs, out var lhs);
                lhs = ResolveInputValue(lhs, ref executionState);
                rhs = ResolveInputValue(rhs, ref executionState);
                inst = $"{cmd2Arg} {lhs.AsIC10} {rhs.AsIC10}";
                CheckFree(ref executionState, rhs);
                CheckFree(ref executionState, lhs);
            }
            else if (!string.IsNullOrEmpty(cmd1Arg))
            {
                executionState.VirtualStack = executionState.VirtualStack.Pop(out var lhs);
                lhs = ResolveInputValue(lhs, ref executionState);
                if (lhs is DeviceStackValue)
                {
                    if (instruction.OpCode == OpCodes.Brtrue || instruction.OpCode == OpCodes.Brtrue_S) { cmd1Arg = "bdse"; }
                    else if (instruction.OpCode == OpCodes.Brfalse || instruction.OpCode == OpCodes.Brfalse_S) { cmd1Arg = "bdns"; }
                    else { throw new InvalidOperationException(); }
                }
                inst = $"{cmd1Arg} {lhs.AsIC10}";
                CheckFree(ref executionState, lhs);
            }

            for (int i = 0; i < instructions.Length; ++i)
            {
                if (instructions[i].Offset == absOffset)
                {
                    return new FragmentText($"{inst} {GetInstructionLabel(i)}", branchTargets: new int[] { i }.ToImmutableArray());
                }
            }
            throw new InvalidOperationException();
        }

        [OpCodeHandler(@"ldc(\.[ir][0-9](\.(m)?[0-9])?)?(\.s)?")]
        private FragmentText Handle_Ldc(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            StackValue valueToPush;
            if (instruction.OpCode == OpCodes.Ldc_I4) { valueToPush = new StaticStackValue { Value = (int)instruction.Data }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_0) { valueToPush = new StaticStackValue { Value = 0 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_1) { valueToPush = new StaticStackValue { Value = 1 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_2) { valueToPush = new StaticStackValue { Value = 2 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_3) { valueToPush = new StaticStackValue { Value = 3 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_4) { valueToPush = new StaticStackValue { Value = 4 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_5) { valueToPush = new StaticStackValue { Value = 5 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_6) { valueToPush = new StaticStackValue { Value = 6 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_7) { valueToPush = new StaticStackValue { Value = 7 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_8) { valueToPush = new StaticStackValue { Value = 8 }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_S) { valueToPush = new StaticStackValue { Value = (byte)instruction.Data }; }
            else if (instruction.OpCode == OpCodes.Ldc_I4_M1) { valueToPush = new StaticStackValue { Value = -1 }; }
            else if (instruction.OpCode == OpCodes.Ldc_R4) { valueToPush = new StaticStackValue { Value = (float)instruction.Data }; }
            else if (instruction.OpCode == OpCodes.Ldc_R8) { valueToPush = new StaticStackValue { Value = (float)(double)instruction.Data }; }
            else throw new InvalidOperationException();
            executionState.VirtualStack = executionState.VirtualStack.Push(valueToPush);
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"stloc(\.[0-9]|\.s)?")]
        private FragmentText Handle_Stloc(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
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
            executionState.VirtualStack = executionState.VirtualStack.Pop(out var value);
            if (value is DeferredExpressionStackValue deferredExpressionStackValue)
            {
                FragmentText fragmentText = FragmentText.Empty;
                if (executionState.LocalVariableRegisterMappings[localVariableIndex] != -1)
                {
                    fragmentText = deferredExpressionStackValue.ExpressionText.Replace("$", $"r{executionState.LocalVariableRegisterMappings[localVariableIndex]}");
                }
                foreach (var deferredInput in deferredExpressionStackValue.FreeValues)
                {
                    CheckFree(ref executionState, deferredInput);
                }
                executionState.LocalVariableKnownStates = executionState.LocalVariableKnownStates.SetItem(localVariableIndex, null);
                return fragmentText;
            }
            executionState.LocalVariableKnownStates = executionState.LocalVariableKnownStates.SetItem(localVariableIndex, value);
            string code = string.Empty;
            if (value is RegisterStackValue registerStackValue)
            {
                // We don't want to leave the register hanging and allocated since we don't really know when this local var will be reused, so do a horrible move
                if (executionState.LocalVariableRegisterMappings[localVariableIndex] != -1)
                {
                    code = $"move r{executionState.LocalVariableRegisterMappings[localVariableIndex]} {value.AsIC10}";
                }
            }
            CheckFree(ref executionState, value);
            return new FragmentText(code);
        }

        [OpCodeHandler(@"ldloc(\.[0-9]|\.s)?")]
        private FragmentText Handle_Ldloc(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
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
            var knownState = executionState.LocalVariableKnownStates[localVariableIndex];
            if (knownState != null)
            {
                executionState.VirtualStack = executionState.VirtualStack.Push(knownState);
                return FragmentText.Empty;
            }
            executionState.VirtualStack = executionState.VirtualStack.Push(new RegisterStackValue { RegisterIndex = executionState.LocalVariableRegisterMappings[localVariableIndex] });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"ldloca(\.s)?")]
        private FragmentText Handle_Ldloca(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            int localVariableIndex;
            if (instruction.OpCode == OpCodes.Ldloca) { localVariableIndex = (int)instruction.Data; }
            else if (instruction.OpCode == OpCodes.Ldloca_S) { localVariableIndex = (sbyte)instruction.Data; }
            else { throw new InvalidOperationException(); }
            if (executionState.LocalVariableKnownStates[localVariableIndex] is DeviceSlotsStackValue deviceSlotsStackValue)
            {
                executionState.VirtualStack = executionState.VirtualStack.Push(deviceSlotsStackValue);
                return FragmentText.Empty;
            }

            throw new NotImplementedException();
        }

        [OpCodeHandler(@"add|sub|mul|div|and|or|xor|shl|shr")]
        private FragmentText Handle_BinaryArithmetic(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop2(out var rhs, out var lhs);
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
                else if (instruction.OpCode == OpCodes.Shl) { result = (int)lhsStatic.Value << (int)rhsStatic.Value; }
                else if (instruction.OpCode == OpCodes.Shr) { result = (int)lhsStatic.Value >> (int)rhsStatic.Value; }
                else if (instruction.OpCode == OpCodes.Shr_Un) { result = (uint)lhsStatic.Value >> (int)rhsStatic.Value; }
                else { throw new InvalidOperationException(); }
                executionState.VirtualStack = executionState.VirtualStack.Push(new StaticStackValue { Value = result });
                return FragmentText.Empty;
            }
            lhs = ResolveInputValue(lhs, ref executionState);
            rhs = ResolveInputValue(rhs, ref executionState);
            string ic10;
            if (instruction.OpCode == OpCodes.Add) { ic10 = "add"; }
            else if (instruction.OpCode == OpCodes.Sub) { ic10 = "sub"; }
            else if (instruction.OpCode == OpCodes.Mul) { ic10 = "mul"; }
            else if (instruction.OpCode == OpCodes.Div) { ic10 = "div"; }
            else if (instruction.OpCode == OpCodes.And) { ic10 = "and"; }
            else if (instruction.OpCode == OpCodes.Or) { ic10 = "or"; }
            else if (instruction.OpCode == OpCodes.Xor) { ic10 = "xor"; }
            else if (instruction.OpCode == OpCodes.Shl) { ic10 = "sll"; }
            else if (instruction.OpCode == OpCodes.Shr) { ic10 = "srl"; }
            else if (instruction.OpCode == OpCodes.Shr_Un) { ic10 = "srl"; }
            else { throw new InvalidOperationException(); }
            executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
            {
                ExpressionText = new FragmentText($"{ic10} $ {lhs.AsIC10} {rhs.AsIC10}"),
                FreeValues = new StackValue[] { lhs, rhs }.ToImmutableArray(),
            });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"not")]
        private FragmentText Handle_UnaryArithmetic(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop(out var rhs);
            rhs = ResolveInputValue(rhs, ref executionState);
            string ic10;
            if (instruction.OpCode == OpCodes.Not) { ic10 = "not"; }
            else { throw new InvalidOperationException(); }
            executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
            {
                ExpressionText = new FragmentText($"{ic10} $ {rhs.AsIC10}"),
                FreeValues = new StackValue[] { rhs }.ToImmutableArray(),
            });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"neg")]
        private FragmentText Handle_Neg(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            executionState.VirtualStack = executionState.VirtualStack.Pop(out var rhs);
            rhs = ResolveInputValue(rhs, ref executionState);
            executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
            {
                ExpressionText = new FragmentText($"sub $ 0 {rhs.AsIC10}"),
                FreeValues = new StackValue[] { rhs }.ToImmutableArray(),
            });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"(ceq|cgt|clt)(\.un)?")]
        private FragmentText Handle_Compare(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop2(out var rhs, out var lhs);
            lhs = ResolveInputValue(lhs, ref executionState);
            rhs = ResolveInputValue(rhs, ref executionState);
            if (((lhs is DeviceStackValue && rhs is NullStackValue) || (rhs is DeviceStackValue && lhs is NullStackValue)) && instruction.OpCode == OpCodes.Cgt_Un)
            {
                var deviceStackValue = (lhs as DeviceStackValue) ?? (rhs as DeviceStackValue);
                if (deviceStackValue == null) { throw new InvalidOperationException(); }
                executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
                {
                    ExpressionText = new FragmentText($"sdse $ {deviceStackValue.AsIC10}"),
                    FreeValues = new StackValue[] { lhs, rhs }.ToImmutableArray(),
                });
                return FragmentText.Empty;
            }
            string ic10;
            if (instruction.OpCode == OpCodes.Ceq) { ic10 = "seq"; }
            else if (instruction.OpCode == OpCodes.Cgt) { ic10 = "sgt"; }
            else if (instruction.OpCode == OpCodes.Cgt_Un) { ic10 = "sgt"; }
            else if (instruction.OpCode == OpCodes.Clt) { ic10 = "slt"; }
            else if (instruction.OpCode == OpCodes.Clt_Un) { ic10 = "slt"; }
            else { throw new InvalidOperationException(); }
            executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
            {
                ExpressionText = new FragmentText($"{ic10} $ {lhs.AsIC10} {rhs.AsIC10}"),
                FreeValues = new StackValue[] { lhs, rhs }.ToImmutableArray(),
            });
            return FragmentText.Empty;
        }

        [OpCodeHandler(@"switch")]
        private FragmentText Handle_Switch(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            executionState.VirtualStack = executionState.VirtualStack.Pop(out var switchValue);
            switchValue = ResolveInputValue(switchValue, ref executionState);
            int[]? offsets = instruction.Data as int[];
            if (offsets == null) { throw new InvalidOperationException(); }
            var sb = new StringBuilder();
            IList<int> branchTargets = new List<int>();
            for (int j = 0; j < offsets.Length; ++j)
            {
                var offset = offsets[j];
                var absOffset = instruction.Offset + instruction.Size + offset;
                bool found = false;
                for (int i = 0; i < instructions.Length; ++i)
                {
                    if (instructions[i].Offset == absOffset)
                    {
                        branchTargets.Add(i);
                        sb.AppendLine($"beq {switchValue.AsIC10} {j} {GetInstructionLabel(i)}");
                        found = true;
                        break;
                    }
                }
                if (!found) { throw new InvalidOperationException(); }
            }
            CheckFree(ref executionState, switchValue);
            return new FragmentText(sb.ToString(), branchTargets.ToImmutableArray());
        }

        [OpCodeHandler(@"ret")]
        private FragmentText Handle_Ret(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            if (method.IsConstructor || method is not MethodInfo methodInfo) { return FragmentText.Empty; }
            if (isInline)
            {
                if (methodInfo.ReturnType == typeof(void))
                {
                    return new FragmentText($"j {localLabelPrefix}_end");
                }
                if (returnStackValue != null)
                {
                    if (returnStackValue is not RegisterStackValue registerStackValue) { throw new InvalidOperationException(); }
                    executionState.VirtualStack = executionState.VirtualStack.Pop(out var retVal);
                    if (retVal is DeferredExpressionStackValue deferredExpressionStackValue)
                    {
                        return deferredExpressionStackValue.ExpressionText.Replace("$", registerStackValue.AsIC10) + new FragmentText($"j {localLabelPrefix}_end");
                    }
                    CheckFree(ref executionState, retVal);
                    return new FragmentText($"move {registerStackValue.AsIC10} {retVal.AsIC10}{Environment.NewLine}j {localLabelPrefix}_end");
                }
                executionState.VirtualStack = executionState.VirtualStack.Pop(out returnStackValue);
                return new FragmentText($"j {localLabelPrefix}_end");
            }
            else
            {
                if (methodInfo.ReturnType == typeof(void))
                {
                    return new FragmentText($"j ra");
                }
                executionState.VirtualStack = executionState.VirtualStack.Pop(out var retVal);
                retVal = ResolveInputValue(retVal, ref executionState);
                CheckFree(ref executionState, retVal);
                return new FragmentText($"push {retVal.AsIC10}{Environment.NewLine}j ra");
            }
            
        }

        [OpCodeHandler(@"conv\..+")]
        private FragmentText Handle_Conv(ReadOnlySpan<Instruction> instructions, int instructionIndex, ref ExecutionState executionState)
        {
            var instruction = instructions[instructionIndex];
            // since ic10 only has f32 registers, the only conv we're interested in emitting for is f -> i
            var iname = instruction.OpCode.Name ?? "";
            if (iname.StartsWith("conv.i") || iname.StartsWith("conv.u"))
            {
                executionState.VirtualStack = executionState.VirtualStack.Pop(out var value);
                value = ResolveInputValue(value, ref executionState);

                executionState.VirtualStack = executionState.VirtualStack.Push(new DeferredExpressionStackValue
                {
                    ExpressionText = new FragmentText($"trunc $ {value.AsIC10}"),
                    FreeValues = new StackValue[] { value }.ToImmutableArray(),
                });
            }
            return FragmentText.Empty;
        }

        private StackValue ResolveInputValue(StackValue stackValue, ref ExecutionState executionState)
        {
            if (stackValue is DeferredExpressionStackValue deferredExpressionStackValue)
            {
                int regIdx = AllocateRegister(ref executionState);
                var reg = new RegisterStackValue { RegisterIndex = regIdx };
                executionState.PendingIntermediateFragments = executionState.PendingIntermediateFragments.Add(deferredExpressionStackValue.ExpressionText.Replace("$", reg.AsIC10));
                foreach (var deferredInput in deferredExpressionStackValue.FreeValues)
                {
                    CheckFree(ref executionState, deferredInput);
                }
                return reg;
            }
            else
            {
                return stackValue;
            }
        }

        private FragmentText GenerateCallSite(string labelPrefix, MethodInfo callTarget, StackValue[] callParams, ref ExecutionState executionState)
        {
            // Try and inline the method first
            var inlineCallText = TryGenerateInlineCallSite(labelPrefix, callTarget, callParams, ref executionState);
            if (inlineCallText != null) { return inlineCallText.Value; }

            // Inline failed, do a regular call stack call instead
            throw new NotImplementedException();

            // inlineContext.FixupCallSites(contexts, inlineOutputWriter);

            /*methodDependencies.Add(method);
            StackValue? returnParam = null;
            if (callTarget.ReturnType != typeof(void))
            {
                var regIdx = AllocateRegister(ref executionState);
                var regValue = new RegisterStackValue { RegisterIndex = regIdx };
                executionState.VirtualStack = executionState.VirtualStack.Push(regValue);
                returnParam = regValue;
            }
            var preReturnRegisterAllocs = executionState.RegisterAllocations;
            CheckFree(ref executionState, callParams);
            return new FragmentText(string.Empty, new CallBinding(method, callParams, returnParam, preReturnRegisterAllocs));*/
        }

        private FragmentText? TryGenerateInlineCallSite(string labelPrefix, MethodInfo callTarget, StackValue[] callParams, ref ExecutionState executionState)
        {
            var inlineContext = new ExecutionContext(compilerOptions, executionState.RegisterAllocations, callTarget, true, callParams.Reverse());
            var instructions = ILView.ToOpCodes(callTarget).ToArray();
            var inlineOutputWriter = new OutputWriter(instructions.Length);
            inlineOutputWriter.LabelPrefix = labelPrefix;
            inlineContext.Compile(instructions, inlineOutputWriter);
            // TODO: Handle compile fail (e.g. went over register or instruction limit)
            if (inlineContext.returnStackValue != null) { executionState.VirtualStack = executionState.VirtualStack.Push(inlineContext.returnStackValue); }
            // TODO: If we're pushing a deferred expression here, it means there's still potentially some registers allocated by the inlined method in use but we're not tracking them in our own execution state
            // We should pull any allocated registers across to ensure we don't accidentally reuse them too early
            return new FragmentText(inlineOutputWriter.ToString());
        }

        private FragmentText? TryGenerateCallStackCallSite(string labelPrefix, MethodInfo callTarget, StackValue[] callParams, ref ExecutionState executionState)
        {
            throw new NotImplementedException();
            //var conflictingRegisters = methodContext.allUsedRegisters & callSite.RegistersState & ~reservedRegisters;
            //var sb = new StringBuilder();
            //for (int i = 0; i < RegisterAllocations.NumTotal; ++i)
            //{
            //    if (conflictingRegisters.IsAllocated(i))
            //    {
            //        sb.AppendLine($"push r{i}");
            //    }
            //}
            //sb.AppendLine("push ra");
            //for (int i = 0; i < callSite.Binding.CallParams.Length; ++i)
            //{
            //    sb.AppendLine($"push {callSite.Binding.CallParams[i].AsIC10}");
            //}
            //sb.AppendLine($"jal {callSite.Binding.TargetMethod.Name}");
            //sb.AppendLine("pop ra");
            //for (int i = 0; i < RegisterAllocations.NumTotal; ++i)
            //{
            //    if (conflictingRegisters.IsAllocated(RegisterAllocations.NumTotal - (i + 1)))
            //    {
            //        sb.AppendLine($"pop r{RegisterAllocations.NumTotal - (i + 1)}");
            //    }
            //}
            //outputWriter.SetCode(callSite.InstructionIndex, sb.ToString().Trim());
        }

        private StackValue[] GetCallParameters(ref ExecutionState executionState, MethodBase method)
        {
            var ps = method.GetParameters();
            var paramValues = new StackValue[ps.Length];
            for (int i = 0; i < ps.Length; ++i)
            {
                executionState.VirtualStack = executionState.VirtualStack.Pop(out paramValues[ps.Length - (i + 1)]);
                paramValues[ps.Length - (i + 1)] = ResolveInputValue(paramValues[ps.Length - (i + 1)], ref executionState);
            }
            return paramValues;
        }

        private int AllocateRegister(ref ExecutionState executionState)
        {
            executionState.RegisterAllocations = executionState.RegisterAllocations.Allocate(out int regIdx);
            allUsedRegisters |= executionState.RegisterAllocations;
            return regIdx;
        }

        private void CheckFree(ref ExecutionState executionState, StackValue stackValue)
        {
            if (stackValue is not RegisterStackValue registerStackValue) { return; }
            int refCount = FindRegisterReferences(ref executionState, registerStackValue.RegisterIndex);
            if (refCount > 0) { return; }
            executionState.RegisterAllocations = executionState.RegisterAllocations.Free(registerStackValue.RegisterIndex);
        }

        private void CheckFree(ref ExecutionState executionState, IEnumerable<StackValue> stackValues)
        {
            foreach (var stackValue in stackValues)
            {
                CheckFree(ref executionState, stackValue);
            }
        }

        private int FindRegisterReferences(ref ExecutionState executionState, int registerIndex)
        {
            int refCount = 0;
            if (reservedRegisters.IsAllocated(registerIndex)) { ++refCount; }
            foreach (var item in executionState.VirtualStack.Stack)
            {
                if (item is RegisterStackValue registerStackValue && registerStackValue.RegisterIndex == registerIndex) { ++refCount; }
            }
            foreach (var localRegisterIndex in executionState.LocalVariableRegisterMappings)
            {
                if (localRegisterIndex == registerIndex) { ++refCount; }
            }
            return refCount;
        }

        private string GetInstructionLabel(int instructionIndex)
            => string.IsNullOrEmpty(localLabelPrefix) ? $"il_{instructionIndex}" : $"{localLabelPrefix}_il_{instructionIndex}";
    }
}
