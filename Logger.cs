using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ESRecorder
{
    static class Logger
    {
        private static StreamWriter? file = null;
        private static Mutex mut = new Mutex();

        public static void Initialise()
        {
            string temp = Path.GetTempPath();
            string f = Path.Combine(temp, "esrecord.log");
            string f0 = Path.Combine(temp, "esrecord.0.log");

            if (File.Exists(f))
            {
                if (File.Exists(f0))
                    File.Delete(f0);

                File.Move(f, f0);
            }

            file = new(File.Open(f, FileMode.Create));
            file.AutoFlush = true;
        }

        public static void WriteRaw(string message)
        {
            mut.WaitOne();

            if (file == null)
            {
                Initialise();
                file.WriteLine("Initialise() was not called. This file was created automatically");
            }

            file.Write(message);
            mut.ReleaseMutex();
        }

        public static void WriteLog(string source, string message)
        {
            mut.WaitOne();
            
            if (file == null)
            {
                Initialise();
                file.WriteLine("Initialise() was not called. This file was created automatically");
            }

            file.WriteLine(DateTime.Now.ToLocalTime().ToString("s") + " :: " + source + " -> " + message);
            mut.ReleaseMutex();
        }

        public static void Close()
        {
            mut.WaitOne();
            if (file == null) return;

            file.Flush();
            file.Close();
            mut.ReleaseMutex();
        }
    }
}
