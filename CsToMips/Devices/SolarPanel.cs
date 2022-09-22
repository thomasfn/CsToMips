namespace CsToMips.Devices
{
    [DeviceInterface(-539224550)]
    public interface ISolarPanel : ICommonDevice
    {
        float Horizontal { get; set; }
        float Vertical { get; set; }
        float Charge { get; }
        float Maximum { get; }
        float Ratio { get; }
    }

    [DeviceInterface(-539224550)]
    public interface IMulticastSolarPanel : ICommonMulticastDevice
    {
        float Horizontal { set; }
        float GetHorizontal(MulticastAggregationMode mode);
        float Vertical { set; }
        float GetVertical(MulticastAggregationMode mode);
        float GetCharge(MulticastAggregationMode mode);
        float GetMaximum(MulticastAggregationMode mode);
        float GetRatio(MulticastAggregationMode mode);
    }
}
