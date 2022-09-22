namespace CsToMips.Devices
{
    [DeviceInterface(0)]
    public interface IGasSensor : ICommonDevice
    {
        float Pressure { get; }
        float Temperature { get; }
        float RatioOxygen { get; }
        float RatioCarbonDioxide { get; }
        float RatioNitrogen { get; }
        float RatioPollutant { get; }
        float RatioVolatiles { get; }
        float RatioWater { get; }
    }

    [DeviceInterface(0)]
    public interface IMulticastGasSensor : ICommonMulticastDevice
    {
        float GetPressure(MulticastAggregationMode mode);
        float GetTemperature(MulticastAggregationMode mode);
        float GetRatioOxygen(MulticastAggregationMode mode);
        float GetRatioCarbonDioxide(MulticastAggregationMode mode);
        float GetRatioNitrogen(MulticastAggregationMode mode);
        float GetRatioPollutant(MulticastAggregationMode mode);
        float GetRatioVolatiles(MulticastAggregationMode mode);
        float GetRatioWater(MulticastAggregationMode mode);
    }
}
