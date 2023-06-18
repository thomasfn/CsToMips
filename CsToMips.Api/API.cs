using System;
using System.Threading;

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

    public enum CompileHintCallType
    {
        CallStack,
        Inline,
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public sealed class CompileHintAttribute : Attribute
    {
        public readonly string Pattern;
        public readonly CompileHintCallType CompileHintCallType;

        public CompileHintAttribute(string pattern, CompileHintCallType compileHintCallType)
        {
            Pattern = pattern;
            CompileHintCallType = compileHintCallType;
        }
    }

    public interface IStationeersProgram
    {
        void Run();
    }

    public static class IC10Helpers
    {
        public static event Action? OnYield;
        public static readonly AutoResetEvent YieldWaitEvent = new AutoResetEvent(false);

        [CompileHint("yield", CompileHintCallType.Inline)]
        public static void Yield()
        {
            OnYield?.Invoke();
            YieldWaitEvent.WaitOne();
        }

        [CompileHint("sleep #0", CompileHintCallType.Inline)]
        public static void Sleep(float seconds) { Yield(); }

        [CompileHint("s db Setting #0", CompileHintCallType.Inline)]
        public static void Debug(float value) { }

        public static int GetTypeHash<T>() { return 0; }

        public static int Hash(string str) { return 0; }

        [CompileHint("sb #0 #1 #2", CompileHintCallType.Inline)]
        public static void StoreBatch(int typeHash, string varName, double value) { }
    }
}
