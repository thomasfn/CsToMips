namespace CsToMips.Programs
{
    using Devices;

#nullable disable

    internal class SolarPanelTracker : IStationeersProgram
    {
        [Device("dDaylightSensor", 0)]
        IStructureDaylightSensor DaylightSensor;

        [MulticastDevice]
        IMulticastStructureSolarPanelDual SolarPanels;

        public void Run()
        {
            DaylightSensor.Mode = StructureDaylightSensorMode.Vertical;
            DaylightSensor.On = true;
            while (true)
            {
                SolarPanels.Horizontal = DaylightSensor.Horizontal + 180.0f;
                SolarPanels.Vertical = DaylightSensor.Vertical / 2.0f;
                IC10Helpers.Yield();
            }
        }
    }
}
