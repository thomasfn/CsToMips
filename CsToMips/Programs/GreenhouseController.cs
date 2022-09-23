namespace CsToMips.Programs
{
    using Devices;

#nullable disable

    internal class GreenhouseController : IStationeersProgram
    {
        enum HeatState
        {
            Idle,
            Heating,
            Cooling
        }

        [Device("dGasSensor", 0)]
        IStructureGasSensor GasSensor;

        [Device("dDaylightSensor", 1)]
        IStructureDaylightSensor DaylightSensor;

        [MulticastDevice]
        IMulticastStructureWallHeater WallHeaters;

        [MulticastDevice]
        IMulticastStructureWallCooler WallCoolers;

        [MulticastDevice]
        IMulticastStructureGrowLight GrowLights;

        HeatState heatState = 0;
        const float TargetTemp = 31.0f;
        const float MinTemp = TargetTemp * 0.95f;
        const float MaxTemp = TargetTemp * 1.05f;

        public void Run()
        {
            WallHeaters.Lock = true;
            WallCoolers.Lock = true;
            while (true)
            {
                CheckTemps();
                CheckGrowLights();
                IC10.Yield();
            }
        }

        private void CheckTemps()
        {
            var currentTemp = KelvinToCelcius(GasSensor.Temperature);
            switch (heatState)
            {
                case HeatState.Cooling:
                    if (currentTemp <= TargetTemp)
                    {
                        heatState = HeatState.Idle;
                    }
                    break;
                case HeatState.Idle:
                    if (currentTemp <= MinTemp)
                    {
                        heatState = HeatState.Heating;
                    }
                    else if (currentTemp >= MaxTemp)
                    {
                        heatState = HeatState.Cooling;
                    }
                    break;
                case HeatState.Heating:
                    if (currentTemp >= TargetTemp)
                    {
                        heatState = HeatState.Idle;
                    }
                    break;
            }
            switch (heatState)
            {
                case HeatState.Cooling: TurnOnCoolers(); break;
                case HeatState.Idle: TurnOffAll(); break;
                case HeatState.Heating: TurnOnHeaters(); break;
            }
        }

        private void CheckGrowLights()
        {
            GrowLights.On = !DaylightSensor.Activate;
        }

        private void TurnOnHeaters()
        {
            WallHeaters.On = true;
            WallCoolers.On = false;
        }

        private void TurnOnCoolers()
        {
            WallHeaters.On = false;
            WallCoolers.On = true;
        }

        private void TurnOffAll()
        {
            WallHeaters.On = false;
            WallCoolers.On = false;
        }

        private float KelvinToCelcius(float kelvin) => kelvin - 273.15f;
    }
}
