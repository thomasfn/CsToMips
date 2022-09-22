namespace CsToMips.Devices
{
    [DeviceInterface(1076425094)]
    public interface IDaylightSensor : ICommonDevice
    {
        int Mode { get; set; }
        bool Activate { get; set; }
        float Horizontal { get; }
        float Vertical { get; }
        float SolarAngle { get; }
    }

    [DeviceInterface(1076425094)]
    public interface IMulticastDaylightSensor : ICommonMulticastDevice
    {
        int Mode { set; }
        int GetMode(MulticastAggregationMode mode);
        bool Activate { set; }
        float GetHorizontal(MulticastAggregationMode mode);
        float GetVertical(MulticastAggregationMode mode);
        float GetSolarAngle(MulticastAggregationMode mode);
    }
}
