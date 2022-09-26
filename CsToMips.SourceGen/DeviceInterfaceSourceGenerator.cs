using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CsToMips.SourceGen
{
    [Generator]
    public class DeviceInterfaceSourceGenerator : ISourceGenerator
    {
        private const string INDENT = "    ";
        private static readonly IReadOnlyDictionary<string, string> overrideLogicTypes = new Dictionary<string, string>
        {
            { "On", "bool" },
            { "Open", "bool" },
            { "Error", "bool" },
            { "Lock", "bool" },
            { "Activate", "bool" },
            { "Occupied", "bool" },
            { "ClearMemory", "bool" },
            { "PrefabHash", "int" },
            { "OccupantHash", "int" },
            { "Quantity", "int" },
            { "MaxQuantity", "int" },
            { "InputCount", "int" },
            { "OutputCount", "int" },
            { "Mode", "int" },
        };

        public void Initialize(GeneratorInitializationContext context)
        {
            
        }

        public void Execute(GeneratorExecutionContext context)
        {
            IEnumerable<AdditionalText> files = context.AdditionalFiles.Where(at => at.Path.EndsWith("PrefabData.json"));
            var sourceBuilder = new StringBuilder();
            sourceBuilder.AppendLine($"using System;");
            sourceBuilder.AppendLine($"namespace CsToMips.Devices");
            sourceBuilder.AppendLine($"{{");
            foreach (var file in files)
            {
                sourceBuilder.AppendLine($"// {file.Path}");
                var text = file.GetText()?.ToString() ?? "";
                if (string.IsNullOrEmpty(text))
                {
                    sourceBuilder.AppendLine("// (no content)");
                    continue;
                }
                var prefabData = JsonSerializer.Deserialize<PrefabData>(file.GetText()?.ToString() ?? "");
                if (prefabData == null)
                {
                    sourceBuilder.AppendLine("// (failed to deserialise)");
                    continue;
                }
                sourceBuilder.AppendLine($"// {prefabData.Things.Count()} things");
                foreach (var thing in prefabData.Things)
                {
                    GenerateThing(thing, sourceBuilder, INDENT);
                }
            }
            sourceBuilder.AppendLine($"}}");
            context.AddSource("Devices.Generated", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
        }

        private string GetLogicTypeClrType(LogicType logicType)
        {
            if (overrideLogicTypes.TryGetValue(logicType.Type, out var overrideType))
            {
                return overrideType;
            }
            return "float";
        }

        private string GetLogicTypeClrType(LogicSlotType logicSlotType)
        {
            if (overrideLogicTypes.TryGetValue(logicSlotType.Type, out var overrideType))
            {
                return overrideType;
            }
            return "float";
        }

        private bool ShouldUseEnumForModes(IEnumerable<string>? modes)
        {
            if (modes == null || !modes.Any()) { return false; }
            if (modes.All(m => int.TryParse(m, out _))) { return false; }
            if (modes.All(m => string.IsNullOrEmpty(CleanModeName(m)))) { return false; }
            return true;
        }

        private string? CleanModeName(string modeName)
        {
            if (string.IsNullOrEmpty(modeName)) { return null; }
            if (modeName.Contains("<") || modeName.Contains(">") || modeName.Contains(":")) { return null; }
            if (int.TryParse(modeName.Substring(0, 1), out _)) { modeName = $"_{modeName}"; }
            modeName = modeName.Replace(" ", "");
            modeName = modeName.Replace("#", "Sharp");
            modeName = modeName.Replace("-", "Neg");
            return modeName;
        }

        private void GenerateThing(Thing thing, StringBuilder sb, string prefix)
        {
            if (thing.Logic == null) { return; }
            if (thing.PrefabName.Contains("(")) { return; }
            bool hasModesEnum = ShouldUseEnumForModes(thing.Modes);
            if (thing.Modes != null && hasModesEnum)
            {
                sb.AppendLine($"{prefix}public enum {thing.PrefabName}Mode");
                sb.AppendLine($"{prefix}{{");
                foreach (var (m, i) in thing.Modes.Select((m, i) => (m, i)))
                {
                    var mode = CleanModeName(m);
                    if (string.IsNullOrEmpty(mode)) { continue; }
                    sb.AppendLine($"{prefix}{INDENT}{mode} = {i},");
                }
                sb.AppendLine($"{prefix}}}");
            }
            if (thing.Logic.LogicSlotTypes.Any())
            {
                sb.AppendLine($"{prefix}public interface I{thing.PrefabName}Slot");
                sb.AppendLine($"{prefix}{{");
                foreach (var logicSlotType in thing.Logic.LogicSlotTypes)
                {
                    var clrType = GetLogicTypeClrType(logicSlotType);
                    sb.AppendLine($"{prefix}{INDENT}{clrType} {logicSlotType.Type} {{ get; }}");
                }
                sb.AppendLine($"{prefix}}}");

            }
            sb.AppendLine($"{prefix}[DeviceInterface({thing.PrefabHash})]");
            sb.AppendLine($"{prefix}public interface I{thing.PrefabName}");
            sb.AppendLine($"{prefix}{{");
            foreach (var logicType in thing.Logic.LogicTypes)
            {
                if (!logicType.CanRead && !logicType.CanWrite) { continue; }
                var clrType = GetLogicTypeClrType(logicType);
                if (logicType.Type == "Mode" && hasModesEnum) { clrType = $"{thing.PrefabName}Mode"; }
                sb.Append($"{prefix}{INDENT}{clrType} {logicType.Type} {{");
                if (logicType.CanRead) { sb.Append(" get;"); }
                if (logicType.CanWrite) { sb.Append(" set;"); }
                sb.AppendLine(" }");
            }
            if (thing.Logic.LogicSlotTypes.Any())
            {
                int slotCount = thing.Logic.LogicSlotTypes
                    .Select(lst => lst.Slots.Count())
                    .Max();
                sb.AppendLine($"{prefix}{INDENT}[DeviceSlotCount({slotCount})]");
                sb.AppendLine($"{prefix}{INDENT}ReadOnlySpan<I{thing.PrefabName}Slot> Slots {{ get; }}");
            }
            sb.AppendLine($"{prefix}}}");
            sb.AppendLine($"{prefix}[DeviceInterface({thing.PrefabHash})]");
            sb.AppendLine($"{prefix}public interface IMulticast{thing.PrefabName}");
            sb.AppendLine($"{prefix}{{");
            foreach (var logicType in thing.Logic.LogicTypes)
            {
                var clrType = GetLogicTypeClrType(logicType);
                if (logicType.Type == "Mode" && hasModesEnum) { clrType = $"{thing.PrefabName}Mode"; }
                if (logicType.CanWrite)
                {
                    sb.Append($"{prefix}{INDENT}{clrType} {logicType.Type} {{");
                    if (logicType.CanWrite) { sb.Append(" set;"); }
                    sb.AppendLine(" }");
                }
                if (logicType.CanRead && clrType == "float")
                {
                    sb.AppendLine($"{prefix}{INDENT}{clrType} Get{logicType.Type}(MulticastAggregationMode mode);");
                }
            }
            sb.AppendLine($"{prefix}{INDENT}");
            sb.AppendLine($"{prefix}}}");
        }



    }
}
