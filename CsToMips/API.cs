namespace CsToMips
{
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = true)]
    public sealed class DeviceAttribute : Attribute
    {
        public readonly string PinName;
        public readonly int PinIndex;

        public DeviceAttribute(string pinName, int pinIndex)
        {
            this.PinName = pinName;
            PinIndex = pinIndex;
        }
    }

    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = true)]
    public sealed class MulticastDeviceAttribute : Attribute
    {
        public MulticastDeviceAttribute()
        {

        }
    }

    public interface IStationeersProgram
    {
        void Run();
    }

    public static class IC10
    {
        public static void Yield() { }

        public static void Sleep(float seconds) { }
    }
}
