namespace CsToMips.Programs
{
    using Devices;

#nullable disable

    internal class StationBatteryMonitor : IStationeersProgram
    {
        [Device("dChargeDisplay", 0)]
        IStructureConsoleLED5 ChargeDisplay;

        [MulticastDevice]
        IMulticastStructureBattery StationBatteries;

        [MulticastDevice]
        IMulticastStructureBatteryLarge LargeStationBatteries;

        public void Run()
        {
            while (true)
            {
                float maxPower = StationBatteries.GetMaximum(MulticastAggregationMode.Sum) + LargeStationBatteries.GetMaximum(MulticastAggregationMode.Sum);
                float actualPower = StationBatteries.GetCharge(MulticastAggregationMode.Sum) + LargeStationBatteries.GetCharge(MulticastAggregationMode.Sum);
                float chargeRatio = actualPower / maxPower;
                ChargeDisplay.Setting = chargeRatio;
                IC10Helpers.Yield();
            }
        }
    }
}
