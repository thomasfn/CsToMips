namespace CsToMips.Devices
{
    [DeviceInterface(-739292323)]
    public interface IWallCooler : ICommonDevice
    {
        bool Lock { get; set; }
        bool Error { get; }
        bool Power { get; }
        int RequiredPower { get; }
    }

    [DeviceInterface(-739292323)]
    public interface IMulticastWallCooler : ICommonMulticastDevice
    {
        bool Lock { set; }
        bool GetError(MulticastAggregationMode mode);
        bool GetPower(MulticastAggregationMode mode);
        int GetRequiredPower(MulticastAggregationMode mode);
    }
}
