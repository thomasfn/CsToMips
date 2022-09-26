namespace CsToMips.Programs
{
    using Devices;

#nullable disable

    internal class StationBatteryMonitor : IStationeersProgram
    {
        [Device("dChargeDisplay", 0)]
        IStructureConsoleLED5 ChargeDisplay;

        [Device("dBackupGen", 1)]
        IStructureSolidFuelGenerator BackupGenerator;

        [Device("dLowCoalAlert", 2)]
        IStructureKlaxon LowCoalSpeaker;

        [MulticastDevice]
        IMulticastStructureBattery StationBatteries;

        [MulticastDevice]
        IMulticastStructureBatteryLarge LargeStationBatteries;

        const float BackupGenTurnOnRatio = 0.2f;
        const float BackupGenTurnOffRatio = 0.5f;

        public void Run()
        {
            while (true)
            {
                float maxPower = StationBatteries.GetMaximum(MulticastAggregationMode.Sum) + LargeStationBatteries.GetMaximum(MulticastAggregationMode.Sum);
                float actualPower = StationBatteries.GetCharge(MulticastAggregationMode.Sum) + LargeStationBatteries.GetCharge(MulticastAggregationMode.Sum);
                float chargeRatio = actualPower / maxPower;
                if (ChargeDisplay != null) { ChargeDisplay.Setting = chargeRatio; }
                if (BackupGenerator != null)
                {
                    if (chargeRatio <= BackupGenTurnOnRatio && !BackupGenerator.On)
                    {
                        BackupGenerator.On = true;
                    }
                    else if (chargeRatio >= BackupGenTurnOffRatio && BackupGenerator.On)
                    {
                        BackupGenerator.On = false;
                    }
                }
                if (LowCoalSpeaker != null) { LowCoalSpeaker.On = (BackupGenerator.Slots[0].Quantity / (float)BackupGenerator.Slots[0].MaxQuantity) < 0.2f; }
                IC10Helpers.Yield();
            }
        }
    }
}
