using System;

namespace CsToMips.Devices
{
    [AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
    public sealed class DeviceInterfaceAttribute : Attribute
    {
        public readonly int TypeHash;

        public DeviceInterfaceAttribute(int typeHash)
        {
            TypeHash = typeHash;
        }
    }

    [AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    public sealed class DeviceSlotCountAttribute : Attribute
    {
        public readonly int SlotCount;

        public DeviceSlotCountAttribute(int slotCount)
        {
            SlotCount = slotCount;
        }
    }

    public enum MulticastAggregationMode
    {
        Average,
        Sum,
        Max,
        Min
    }
}
