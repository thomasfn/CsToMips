namespace CsToMips.Programs
{
    using Devices;

#nullable disable

    internal class SolarPanelTracker : IStationeersProgram
    {
        [Device("dDaylightSensor", 0)]
        IDaylightSensor DaylightSensor;

        [MulticastDevice]
        IMulticastSolarPanel SolarPanels;

        public void Run()
        {
            DaylightSensor.Mode = 2;
            DaylightSensor.On = true;
            while (true)
            {
                SolarPanels.Horizontal = DaylightSensor.Horizontal + 180.0f;
                SolarPanels.Vertical = DaylightSensor.Vertical / 2.0f;
                IC10.Yield();
            }
        }
    }
}
