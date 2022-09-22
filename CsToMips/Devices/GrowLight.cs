namespace CsToMips.Devices
{
    [DeviceInterface(-1758710260)]
    public interface IGrowLight : ICommonDevice
    {
        bool Power { get; }
        int RequiredPower { get; }
    }

    [DeviceInterface(-1758710260)]
    public interface IMulticastGrowLight : ICommonMulticastDevice
    {
        bool GetPower(MulticastAggregationMode mode);
        int GetRequiredPower(MulticastAggregationMode mode);
    }
}
