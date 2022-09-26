using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsToMips.SourceGen
{
    public class Thing
    {
        [JsonPropertyName("prefabName")]
        public string PrefabName { get; set; } = "";

        [JsonPropertyName("prefabHash")]
        public int PrefabHash { get; set; }

        [JsonPropertyName("prefabType")]
        public string PrefabType { get; set; } = "";

        [JsonPropertyName("modes")]
        public IEnumerable<string>? Modes { get; set; }

        [JsonPropertyName("logic")]
        public LogicData? Logic { get; set; }
    }

    public class LogicData
    {
        [JsonPropertyName("logicTypes")]
        public IEnumerable<LogicType> LogicTypes { get; set; } = Enumerable.Empty<LogicType>();

        [JsonPropertyName("logicSlotTypes")]
        public IEnumerable<LogicSlotType> LogicSlotTypes { get; set; } = Enumerable.Empty<LogicSlotType>();
    }

    public class LogicType
    {
        [JsonPropertyName("logicType")]
        public string Type { get; set; } = "";

        [JsonPropertyName("canRead")]
        public bool CanRead { get; set; }

        [JsonPropertyName("canWrite")]
        public bool CanWrite { get; set; }
    }

    public class LogicSlotType
    {
        [JsonPropertyName("logicSlotType")]
        public string Type { get; set; } = "";

        [JsonPropertyName("slots")]
        public IEnumerable<LogicSlot> Slots { get; set; } = Enumerable.Empty<LogicSlot>();
    }

    public class LogicSlot
    {
        [JsonPropertyName("slotIndex")]
        public int SlotIndex { get; set; }

        [JsonPropertyName("canRead")]
        public bool CanRead { get; set; }
    }

    public class PrefabData
    {
        [JsonPropertyName("things")]
        public IEnumerable<string> things { get; set; } = Enumerable.Empty<string>();

        public IEnumerable<Thing> Things
        {
#pragma warning disable CS8619 // Nullability of reference types in value doesn't match target type.
            get => things.Select(str => JsonSerializer.Deserialize<Thing>(str)).Where(obj => obj != null);
#pragma warning restore CS8619 // Nullability of reference types in value doesn't match target type.
            set => things = value.Select(obj => JsonSerializer.Serialize(obj));
        }
    }
}
