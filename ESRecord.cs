using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ESRecorder
{
    public enum ESRecordState
    {
        Idle,
        Compiling,
        Preparing,
        Warmup,
        Recording
    }

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]
    public struct SampleConfig
    {
        public bool overrideRevlimit;
        public int prerunCount;
        public int rpm, throttle, frequency, length;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string output;
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleResult
    {
        public bool success;
        public float power, torque, ratio;
        public long millis;
    };

    public static class ESRecord
    {

        [DllImport("es/esrecord-lib.dll")]
        public static extern bool ESRecord_Compile(string path);

        [DllImport("es/esrecord-lib.dll")]
        public static extern bool ESRecord_Initialise();

        [DllImport("es/esrecord-lib.dll")]
        public static extern double ESRecord_Update(float averagefps);

        [DllImport("es/esrecord-lib.dll")]
        public static extern ESRecordState ESRecord_GetState(out int progress);

        [DllImport("es/esrecord-lib.dll")]
        public static extern int ESRecord_GetVersion();

        [DllImport("es/esrecord-lib.dll")]
        public static extern unsafe string ESRecord_Engine_GetName();

        [DllImport("es/esrecord-lib.dll")]
        public static extern unsafe float ESRecord_Engine_GetRedline();

        [DllImport("es/esrecord-lib.dll")]
        public static extern unsafe float ESRecord_Engine_GetDisplacement();

        [DllImport("es/esrecord-lib.dll")]
        public static extern unsafe SampleResult ESRecord_Record(SampleConfig config);

    }
}
