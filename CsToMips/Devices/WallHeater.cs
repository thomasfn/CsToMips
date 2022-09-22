namespace CsToMips.Devices
{
    [DeviceInterface(24258244)]
    public interface IWallHeater : ICommonDevice
    {
        bool Lock { get; set; }
        bool Error { get; }
        bool Power { get; }
        int RequiredPower { get; }
    }

    [DeviceInterface(24258244)]
    public interface IMulticastWallHeater : ICommonMulticastDevice
    {
        bool Lock { set; }
        bool GetError(MulticastAggregationMode mode);
        bool GetPower(MulticastAggregationMode mode);
        int GetRequiredPower(MulticastAggregationMode mode);
    }
}
