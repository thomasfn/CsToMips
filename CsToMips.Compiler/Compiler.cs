using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CsToMips.Compiler
{
    public readonly struct CompilerOptions
    {
        public readonly bool ShouldOptimise;
        public readonly bool AllowInlining;

        public static readonly CompilerOptions Default = new CompilerOptions(true, true);

        public CompilerOptions(bool shouldOptimise, bool allowInlining)
        {
            ShouldOptimise = shouldOptimise;
            AllowInlining = allowInlining;
        }
    }

    public class Compiler
    {
        private readonly Type programType;
        private readonly CompilerOptions options;
        private readonly StringBuilder ic10Stream;

        public Compiler(Type programType, CompilerOptions options)
        {
            if (!typeof(IStationeersProgram).IsAssignableFrom(programType)) { throw new ArgumentException($"Program must implement IStationeersProgram", nameof(programType)); }
            this.programType = programType;
            this.options = options;
            ic10Stream = new StringBuilder();
        }

        public string Compile()
        {
            ic10Stream.Clear();
            var runMethod = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Instance);
            if (runMethod == null) { throw new InvalidOperationException(); }
            var ctor = programType.GetConstructor(Array.Empty<Type>());
            RegisterAllocations reservedRegisters = default;
            foreach (var field in programType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var deviceAttr = field.GetCustomAttribute<DeviceAttribute>();
                if (deviceAttr != null)
                {
                    ic10Stream.AppendLine($"alias {deviceAttr.PinName} d{deviceAttr.PinIndex}");
                    continue;
                }
                var multicastDeviceAttr = field.GetCustomAttribute<MulticastDeviceAttribute>();
                if (multicastDeviceAttr == null)
                {
                    reservedRegisters = reservedRegisters.Allocate(out int regIdx);
                    ic10Stream.AppendLine($"alias {field.Name} r{regIdx}");
                }
            }
            var methodContextMap = new Dictionary<MethodBase, (ExecutionContext, OutputWriter)>();
            if (ctor != null)
            {
                CompileMethod("ctor", ctor, reservedRegisters, methodContextMap);
            }
            CompileMethod("main", runMethod, reservedRegisters, methodContextMap);

            foreach (var pair in methodContextMap)
            {
                var methodName = pair.Key == runMethod ? "main" : pair.Key == ctor ? "ctor" : pair.Key.Name;
                if (methodName != "ctor")
                {
                    ic10Stream.AppendLine($"{methodName}:");
                }
                ic10Stream.AppendLine(pair.Value.Item2.ToString().Trim());
                if (methodName == "ctor")
                {
                    ic10Stream.AppendLine("jal main");
                    ic10Stream.AppendLine("j end");
                }
            }
            ic10Stream.AppendLine("end:");
            var ic10 = ic10Stream.ToString();
            return options.ShouldOptimise ? new Optimiser().Optimise(ic10) : ic10;
        }

        private void CompileMethod(string? methodName, MethodBase method, RegisterAllocations reservedRegisters, IDictionary<MethodBase, (ExecutionContext, OutputWriter)> methodContextMap)
        {
            var instructions = ILView.ToOpCodes(method).ToArray();
            var outputWriter = new OutputWriter(instructions.Length);
            outputWriter.LabelPrefix = methodName ?? "";
            var context = new ExecutionContext(options, reservedRegisters, method, false);
            methodContextMap.Add(method, (context, outputWriter));
            context.Compile(instructions, outputWriter);
            foreach (var depMethod in context.MethodDependencies)
            {
                if (methodContextMap.ContainsKey(depMethod)) { continue; }
                CompileMethod(depMethod.Name, depMethod, reservedRegisters, methodContextMap);
            }
        }

    }
}
