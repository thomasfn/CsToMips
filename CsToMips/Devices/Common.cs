namespace CsToMips.Devices
{
    [AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
    public sealed class DeviceInterfaceAttribute : Attribute
    {
        public readonly int TypeHash;

        public DeviceInterfaceAttribute(int typeHash)
        {
            this.TypeHash = typeHash;
        }
    }

    public enum MulticastAggregationMode
    {
        Sum,
        Average,
        Max,
        Min
    }
}
