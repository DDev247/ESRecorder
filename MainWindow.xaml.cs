using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml;

namespace ESRecorder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public List<SampleRPM> SampleRPMList = new();
        public List<SampleThrottle> SampleThrottleList = new();

        public string EngineName = "";

        public bool finishedLoading = false;
        private ESRecordState lastState = ESRecordState.Idle;
        public bool Running = true;

        private bool recording = false;

        // Throttle -> RPM -> Power + Torque
        public Dictionary<int, Dictionary<int, KeyValuePair<float, float>>> Dyno = new();

        public static System.Globalization.NumberFormatInfo NUM_INFO = System.Globalization.NumberFormatInfo.InvariantInfo;

        public List<KeyValuePair<string, string>> Sounds = new();
        public List<RecordedEngine> RecordedEngines = new();

        [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PathFindOnPathW([In, Out] char[] pszFile, IntPtr ppszOtherDirs);

        public const string VERSION = "0.1.1";

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists("./engines"))
                Directory.CreateDirectory("./engines");
            if (!Directory.Exists("./exports"))
                Directory.CreateDirectory("./exports");

            if(!Directory.Exists("./es") || !File.Exists("./es/esrecord-lib.dll"))
            {
                MessageBox.Show("ES not found. Something is wrong with your installation.", "Oops?", MessageBoxButton.OK, MessageBoxImage.Error);

                this.Close();
                return;
            }

            Load_Data();

            Generate_SampleRPMGrid();
            Generate_SampleThrottleGrid();

            Start_Checking();
            Load_Engine();

            Start_Dyno();

            TitlebarNameVersion.Content = "ESRecorder " + VERSION;
            TitlebarNameVersion.ToolTip = "DLL: " + ESRecord.ESRecord_GetVersion();
            Check_Updates();

            Load_Recorded_Engines();
            Convert_Log.Text = "Idle...";

            Load_Starter_Data();
            Load_Data_Convert();
            finishedLoading = true;
        }

        private void Check_Updates()
        {
            Thread t = new(async () =>
            {
                HttpClient c = new();
                c.Timeout = TimeSpan.FromSeconds(10);

                // try five times
                for (int _ = 0; _ < 5; _++)
                {
                    HttpResponseMessage message = await c.GetAsync("https://raw.githubusercontent.com/DDev247/ESRecorder/refs/heads/main/version");
                    if(!message.IsSuccessStatusCode)
                    {
                        MessageBoxResult res = MessageBox.Show($"Failed to check for new version. Press yes to try again.", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (res != MessageBoxResult.Yes)
                            break;
                    }

                    string s = await message.Content.ReadAsStringAsync();
                    if(s != VERSION)
                    {
                        MessageBoxResult res = MessageBox.Show($"There's a new version available: \"{s}\". Press yes to go to the latest release page.", "New version available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if(res == MessageBoxResult.Yes)
                            Process.Start("start", "https://github.com/DDev247/ESRecorder/releases/latest");
                        break;
                    }
                    else // up to date?
                        break;
                }
            });
            t.Start();
        }

        // ?????
        private void Open_In(string file)
        {
            string fileName = "code";
            char[] filePath = new char[260]; // MAX_PATH
            fileName.CopyTo(0, filePath, 0, fileName.Length);

            bool result = PathFindOnPathW(filePath, IntPtr.Zero);

            if (result)
            {
                Process.Start("code", fileName);
            }
            else
            {
                Process.Start("notepad.exe", fileName);
            }
        }

        private void Convert_Recorded_Engine()
        {
            if (RecordedEngines.Count == 0 || Convert_RecordedEngineSelection.SelectedIndex == -1) {
                MessageBox.Show("No recorded engines, or something went wrong.");
                return;
            }

            if (!Directory.Exists("./exports"))
                Directory.CreateDirectory("./exports");

            RecordedEngine e = RecordedEngines[Convert_RecordedEngineSelection.SelectedIndex];

            Convert_Log.Text = "Converting " + e.Name;

            int idleRpm = -1, maxRpm = -1;
            float staticFriction = -1, dynamicFriction = -1;
            string soundEventName = Sounds[Convert_StarterSoundSelection.SelectedIndex].Key;

            int.TryParse(Convert_IdleRPM.Text, out idleRpm);
            int.TryParse(Convert_MaximumRPM.Text, out maxRpm);
            float.TryParse(Convert_StaticFriction.Text, NUM_INFO, out staticFriction);
            float.TryParse(Convert_DynamicFriction.Text, NUM_INFO, out dynamicFriction);

            if (idleRpm == -1 || maxRpm == -1 || staticFriction == -1 || dynamicFriction == -1)
            {
                MessageBox.Show("Failed to parse a parameter. Please check that none of your parameters are red", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // interpolate from top dyno rpm to maximum rpm
            SortedDictionary<int, float> torque = new();

            // fill in `torque` with e.Dyno100
            foreach (var rpm in e.Dyno100.Keys)
            {
                float t = e.Dyno100[rpm].Value;
                torque[rpm] = t;
            }

            // interpolate down to 200 rpm (so the starter works)
            int delta = e._MinRPM - 200; // ex. 500 - 200 = 300; 100 - 200 = -100
            if (delta > 0)
            {
                // uhh
                float torq = e._MinTorque;
                float i = 1.0f;

                while (delta > 0)
                {
                    float t = torq / i;
                    if (delta < 600)
                    {
                        torque[200] = t;
                        e._MinRPM = 200;
                    }
                    else
                    {
                        torque[e._MinRPM - 500] = t;
                        e._MinRPM -= 500;
                    }

                    i += 0.1f;
                    delta = e._MinRPM - 200;
                }
            }

            // interpolate top
            delta = maxRpm - e._MaxRPM; // ex. 7500 - 7200 = 300; 8000 - 7200 = 800; 8000 - 9000 = -1000
            
            // at least 500 rpm breathing room
            if (delta >= -500)
            {
                float torq = torque[e._MaxRPM];
                float i = 1.1f;
                while (delta > -500)
                {
                    float t = torq / i;

                    e._MaxRPM += 500;
                    torque[e._MaxRPM] = t; // i dont care

                    i += 0.1f;
                    delta = maxRpm - e._MaxRPM;
                }
            }

            // plot this
            List<float> rpm_axisX = new();
            List<float> torque_axisY = new();
            foreach (var rpm in torque.Keys)
            {
                rpm_axisX.Add(rpm);
                torque_axisY.Add(torque[rpm]);
            }

            var scatterTorque = Convert_Plot.Plot.Add.Scatter(rpm_axisX, torque_axisY);
            Convert_Plot.Refresh();

            // apply friction "correction"
            for (int i = 0; i < torque.Count; i++)
            {
                int rpm = torque.Keys.ElementAt(i);
                torque[rpm] = torque[rpm] - staticFriction - ((rpm / 9.55f) * dynamicFriction);
            }

            // output to jbeam

            StreamWriter file = new(File.OpenWrite("./exports/" + Blendify(e.Name) + ".jbeam"));
            file.WriteLine("\"torque\": [");
            file.WriteLine("    [\"rpm\", \"torque\"],");
            foreach (var rpm in torque.Keys)
            {
                file.WriteLine($"    [{rpm}, {torque[rpm].ToString(NUM_INFO)}],");
            }
            file.WriteLine("],");
            file.WriteLine($"\"idleRPM\": {idleRpm.ToString(NUM_INFO)},");
            file.WriteLine($"\"maxRPM\": {maxRpm.ToString(NUM_INFO)},");
            file.WriteLine($"\"friction\": {staticFriction.ToString(NUM_INFO)},");
            file.WriteLine($"\"dynamicFriction\": {dynamicFriction.ToString(NUM_INFO)},");
            file.WriteLine("");
            file.WriteLine("");
            file.WriteLine($"\"starterSample\": \"event:>Engine>Starter>{soundEventName}_eng\",");
            file.WriteLine($"\"starterSampleExhaust\": \"event:>Engine>Starter>{soundEventName}_exh\",");
            file.WriteLine($"\"shutOffSampleEngine\": \"event:>Engine>Shutoff>{soundEventName}_eng\",");
            file.WriteLine($"\"shutOffSampleExhaust\": \"event:>Engine>Shutoff>{soundEventName}_exh\",");
            file.Flush();
            file.Close();

            // output to sfxBlend2D

            file = new(File.OpenWrite("./exports/" + Blendify(e.Name) + ".sfxBlend2D.json"));
            file.WriteLine("{");
            file.WriteLine("    \"header\": {");
            file.WriteLine("        \"version\": 1");
            file.WriteLine("    },");
            file.WriteLine("    \"eventName\": \"event:>Engine>default\",");
            file.WriteLine("    \"samples\": [");
            file.WriteLine("        [");
            int j = 0;
            if (!Directory.Exists("./exports/" + Blendify(e.Name)))
                Directory.CreateDirectory("./exports/" + Blendify(e.Name));
            else
            {
                foreach(var f in Directory.EnumerateFiles("./exports/" + Blendify(e.Name)))
                    File.Delete(f);
            }

            foreach (var rpm in e.Dyno0.Keys)
            {
                string comma = (j < e.Dyno0.Keys.Count - 1) ? "," : "";
                File.Copy("./engines/" + e.Name + "/" + e.Name + "_" + rpm + "_0.wav", "./exports/" + Blendify(e.Name) + "/" + Blendify(e.Name) + "_" + rpm + "_0.wav");
                file.WriteLine($"            [\"art/sound/engine/{Blendify(e.Name)}/{Blendify(e.Name)}_{rpm}_0.wav\", {rpm}]{comma}");
                j++;
            }
            file.WriteLine("        ],");
            file.WriteLine("        [");
            j = 0;
            foreach (var rpm in e.Dyno100.Keys)
            {
                string comma = (j < e.Dyno100.Keys.Count - 1) ? "," : "";
                File.Copy("./engines/" + e.Name + "/" + e.Name + "_" + rpm + "_100.wav", "./exports/" + Blendify(e.Name) + "/" + Blendify(e.Name) + "_" + rpm + "_100.wav");
                file.WriteLine($"            [\"art/sound/engine/{Blendify(e.Name)}/{Blendify(e.Name)}_{rpm}_100.wav\", {rpm}]{comma}");
                j++;
            }
            file.WriteLine("        ]");
            file.WriteLine("    ]");
            file.WriteLine("}");
            file.Flush();
            file.Close();

            MessageBox.Show($"Put your samples from \"exports/{Blendify(e.Name)}/\" into \"/art/sound/engine/{Blendify(e.Name)}\" for it to work properly.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);

            Load_Recorded_Engines();
        }

        private void Load_Recorded_Engines()
        {
            Convert_RecordedEngineSelection.Items.Clear();
            RecordedEngines.Clear();

            string[] files = Directory.GetFiles("./engines/", "*.engine");
            foreach (var file in files)
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                
                if(File.Exists("./engines/" + name + ".csv"))
                {
                    BinaryReader br = new(File.OpenRead(file));
                    string s = new(br.ReadChars(6)); // header of some sorts
                    if (s != "esreng")
                    {
                        MessageBox.Show("Invalid engine file", "Error");
                        continue;
                    }
                    br.ReadInt32(); // skip the shiz

                    RecordedEngine e = RecordedEngine.Load(name);
                    e.Displacement = br.ReadSingle();
                    e.Redline = br.ReadInt32();
                    
                    // engine doesnt have both 0 and 100 throttle curves
                    if (e.Name != "-")
                    {
                        e.Name = br.ReadString();
                        RecordedEngines.Add(e);

                        ComboBoxItem item = new();
                        item.Content = e.Name;

                        Convert_RecordedEngineSelection.Items.Add(item);
                    }

                    br.Close();
                }
            }

            if (RecordedEngines.Count > 0)
            {
                Convert_RecordedEngineSelection.SelectedIndex = 0;
            }

            Start_Convert_Dyno();
        }

        #region Convert screen

        private void Refresh_Convert_Dyno()
        {
            if (Convert_Plot == null) return;
            Convert_Plot.Plot.Clear();

            if (RecordedEngines.Count < 1 || Convert_RecordedEngineSelection.SelectedIndex == -1) goto refresh_convert_dyno_end;

            RecordedEngine e = RecordedEngines[Convert_RecordedEngineSelection.SelectedIndex];

            Convert_EngineName.Text = e.Name;
            Convert_EngineDisplacement.Text = Math.Round(e.Displacement, 1).ToString(NUM_INFO) + "L";

            List<float> rpm_axisX = new();
            List<float> power_axisY = new();
            List<float> torque_axisY = new();

            int rpm_min = int.MaxValue, rpm_max = int.MinValue;

            foreach (var item in e.Dyno100)
            {
                if(item.Key < rpm_min) rpm_min = item.Key;
                if (item.Key > rpm_max) rpm_max = item.Key;

                rpm_axisX.Add(item.Key);
                power_axisY.Add(item.Value.Key);
                torque_axisY.Add(item.Value.Value);
            }

            Convert_EngineInfo.Text = "Minimum RPM: " + rpm_min + "\n";
            Convert_EngineInfo.Text += "Maximum RPM: " + rpm_max + "\n";


            var scatterPower = Convert_Plot.Plot.Add.Scatter(rpm_axisX, power_axisY);
            var scatterTorque = Convert_Plot.Plot.Add.Scatter(rpm_axisX, torque_axisY);

            var crosshairMin = Convert_Plot.Plot.Add.Crosshair(int.Parse(Convert_IdleRPM.Text), 0);
            var crosshairMax = Convert_Plot.Plot.Add.Crosshair(int.Parse(Convert_MaximumRPM.Text), 0);
            
            crosshairMin.HorizontalLine.IsVisible = false;
            crosshairMax.HorizontalLine.IsVisible = false;

            scatterPower.LegendText = "Power";
            scatterTorque.LegendText = "Torque";

        refresh_convert_dyno_end:
            Convert_Plot.Plot.Axes.AutoScale();
            Convert_Plot.Refresh();
        }

        private void Start_Convert_Dyno()
        {
            Convert_Plot.Plot.Grid.LineColor = new("#26282a");
            Convert_Plot.Plot.FigureBackground.Color = new("#0E1012");
            Convert_Plot.Plot.Axes.Color(new("#ffffff"));
            Convert_Plot.Plot.Axes.AntiAlias(true);
            
            Convert_Plot.Plot.Legend.BackgroundColor = ScottPlot.Color.FromARGB(0);
            Convert_Plot.Plot.Legend.FontColor = new("#ffffff");
            Convert_Plot.Plot.Legend.OutlineColor = ScottPlot.Color.FromARGB(0);

            Refresh_Convert_Dyno();
        }

        private void Load_Starter_Data()
        {
            Convert_StarterSoundSelection.Items.Clear();
            Sounds.Clear();

            XmlDocument doc = new XmlDocument();
            doc.Load("./sounds.xml");

            if (doc.DocumentElement == null) return;

            foreach (XmlNode sound in doc.DocumentElement.ChildNodes)
            {
                if (sound.NodeType != XmlNodeType.Element) continue;

                string eventname = sound.Attributes["EventName"].Value;
                string prettyname = sound.Attributes["PrettyName"].Value;

                ComboBoxItem item = new ComboBoxItem();
                item.Content = prettyname; 

                Convert_StarterSoundSelection.Items.Add(item);
                Sounds.Add(new KeyValuePair<string, string>(eventname, prettyname));
            }
            
            Convert_StarterSoundSelection.SelectedIndex = 0;
        }

        #endregion

        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                this.LogTextBlock.Text += message;
                this.LogScrollViewer.ScrollToBottom();
            });
        }

        #region Dyno

        private void Refresh_Dyno()
        {
            DynoPlot.Plot.Clear();
            float powerMax = float.MinValue, powerRpm = 0;
            float torqueMax = float.MinValue, torqueRpm = 0;
            
            foreach (var key in Dyno.Keys)
            {
                List<float> rpm_axisX = new();
                List<float> power_axisY = new();
                List<float> torque_axisY = new();

                foreach (var item in Dyno[key])
                {
                    rpm_axisX.Add(item.Key);

                    power_axisY.Add(item.Value.Key);
                    if (item.Value.Key > powerMax)
                    {
                        powerMax = item.Value.Key;
                        powerRpm = item.Key;
                    }

                    torque_axisY.Add(item.Value.Value);
                    if (item.Value.Value > torqueMax)
                    {
                        torqueMax = item.Value.Value;
                        torqueRpm = item.Key;
                    }
                }

                var scatterPower = DynoPlot.Plot.Add.Scatter(rpm_axisX, power_axisY);
                var scatterTorque = DynoPlot.Plot.Add.Scatter(rpm_axisX, torque_axisY);

                scatterPower.LegendText = "Power: " + key + "%";
                scatterTorque.LegendText = "Torque: " + key + "%";
            }

            DynoPlot.Plot.Axes.AutoScale();
            DynoPlot.Refresh();
        }

        private void Start_Dyno()
        {
            DynoPlot.Plot.Grid.LineColor = new("#26282a");
            DynoPlot.Plot.FigureBackground.Color = new("#0E1012");
            DynoPlot.Plot.Axes.Color(new("#ffffff"));
            DynoPlot.Plot.Axes.AntiAlias(true);

            DynoPlot.Plot.Legend.BackgroundColor = ScottPlot.Color.FromARGB(0);
            DynoPlot.Plot.Legend.FontColor = new("#ffffff");
            DynoPlot.Plot.Legend.OutlineColor = ScottPlot.Color.FromARGB(0);

            Refresh_Dyno();
        }

        #endregion

        private void Load_Engine()
        {
            // Load engine

            this.TitlebarContext.Content = "Loading engine...";
            this.InfoEngineName.Text = "Loading...";
            this.InfoEngineStats.Text = "Loading...";
            this.LogTextBlock.Text = "";
            AddLog("Loading Engine...");

            Thread t = new Thread(() =>
            {
                bool result = ESRecord.ESRecord_Compile("./es/assets/main.mr");
                if (!result)
                {
                    string errorLog = File.ReadAllText("error_log.log");
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        AddLog(" ERROR");
                        this.InfoEngineStats.Text = "Failed to load:\n" + errorLog;
                        this.InfoEngineName.Text = "Failed to load";
                        this.TitlebarContext.Content = "Failed to load engine";
                    });

                    MessageBox.Show("Failed to load engine.\nError log:\n" + errorLog, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);

                    return;
                }

                EngineName = ESRecord.ESRecord_Engine_GetName();

                float redline = ESRecord.ESRecord_Engine_GetRedline();
                float displacement = ESRecord.ESRecord_Engine_GetDisplacement();
                displacement = (float) Math.Round(displacement, 1);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    this.InfoEngineStats.Text = "Redline: " + redline.ToString(provider: NUM_INFO) + " RPM";
                    this.InfoEngineStats.Text += "\nDisplacement: " + displacement.ToString(provider: NUM_INFO) + "L";

                    this.InfoEngineName.Text = EngineName;
                    this.InfoEngineDisplacement.Text = displacement.ToString(provider: NUM_INFO) + "L";
                    this.TitlebarContext.Content = "Loaded: " + EngineName;
                    AddLog(" OK");
                });
            });

            t.Start();
        }

        private string Sanitize_Path(string path)
        {
            return string.Join("_", path.Split(System.IO.Path.GetInvalidFileNameChars()));
        }

        private string Blendify(string path)
        {
            return new string(Sanitize_Path(path).Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-').ToArray()).Replace(' ', '_');
        }

        private void Start_Recording()
        {
            // Start recording
            AddLog("\nStarting recording...");
            string sanitizedName = Sanitize_Path(EngineName);

            if (!Directory.Exists("./engines/"))
                Directory.CreateDirectory("./engines/");

            if (!Directory.Exists("./engines/" + sanitizedName))
                Directory.CreateDirectory("./engines/" + sanitizedName);

            int sampleLength = -1;
            bool success = int.TryParse(SampleLength.Text, out sampleLength);
            if(!success)
            {
                MessageBox.Show("Failed to parse \"" + SampleLength.Text + "\" as integer", "Error");
                return;
            }

            success = int.TryParse(WarmupCount.Text, out int prerun);
            if (!success)
            {
                MessageBox.Show("Failed to parse \"" + WarmupCount.Text + "\" as integer", "Error");
                return;
            }


            // clear dyno
            Dyno.Clear();
            foreach (var sample in SampleThrottleList)
                Dyno.Add(sample.Throttle, new Dictionary<int, KeyValuePair<float, float>>());

            Refresh_Dyno();

            Thread t = new Thread(() =>
            {
                Stopwatch sw = new();
                sw.Start();

                var f = File.CreateText("./engines/" + sanitizedName + ".csv");
                f.WriteLine("rpm,throttle,power_hp,torque_nm");

                for (int i = 0; i < SampleRPMList.Count; i++)
                {
                    if (!recording)
                        break;

                    for (int j = 0; j < SampleThrottleList.Count; j++)
                    {
                        if(!recording)
                            break;

                        SampleConfig sample = new SampleConfig
                        {
                            prerunCount = prerun, // TODO: Make this customisable
                            rpm = SampleRPMList[i].RPM,
                            throttle = SampleThrottleList[j].Throttle,
                            frequency = SampleRPMList[i].Frequency,
                            length = sampleLength, // TODO: Make this customisable
                            overrideRevlimit = true
                        };
                        sample.output = "./engines/" + sanitizedName + "/" + sanitizedName + "_" + SampleRPMList[i].RPM.ToString(provider: NUM_INFO) + "_" + SampleThrottleList[j].Throttle.ToString(provider: NUM_INFO) + ".wav";

                        AddLog("\nRecording rpm=" + sample.rpm.ToString(provider: NUM_INFO) + " throttle=" + sample.throttle.ToString(provider: NUM_INFO) + " frequency=" + sample.frequency.ToString(provider: NUM_INFO) + "...");

                        SampleResult result = ESRecord.ESRecord_Record(sample);
                        
                        AddLog(" OK (" + result.millis + "ms, " + Math.Round(result.ratio, 2) + "x)");

                        // add to RAM dyno
                        if (Dyno[sample.throttle].ContainsKey(sample.rpm))
                            MessageBox.Show("You have duplicate RPM values.");
                        else
                        {
                            Dyno[sample.throttle].Add(sample.rpm, new KeyValuePair<float, float>(result.power, result.torque));
                            Refresh_Dyno();

                            f.WriteLine(sample.rpm.ToString(provider: NUM_INFO) + "," + sample.throttle.ToString(provider: NUM_INFO) + "," + Math.Round(result.power, 3).ToString(provider: NUM_INFO) + "," + Math.Round(result.torque, 3).ToString(provider: NUM_INFO));
                        }
                    }
                }

                f.Flush();
                f.Close();

                BinaryWriter bw = new(File.OpenWrite("./engines/" + sanitizedName + ".engine"));

                bw.Write(Encoding.ASCII.GetBytes("esreng")); // header
                bw.Write((UInt16)SampleRPMList.Count);
                bw.Write((UInt16)SampleThrottleList.Count);
                bw.Write(ESRecord.ESRecord_Engine_GetDisplacement());
                bw.Write(ESRecord.ESRecord_Engine_GetRedline());
                bw.Write(EngineName);

                foreach (var sample in SampleRPMList)
                    bw.Write(sample.RPM);

                foreach (var sample in SampleThrottleList)
                    bw.Write((sbyte)sample.Throttle);

                bw.Flush();
                bw.Close();
                sw.Stop();

                recording = false;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StartRecordingButton.Content = "Start Recording";
                    AddLog("\nFinished recording in " + sw.ElapsedMilliseconds + "ms");
                    Load_Recorded_Engines();
                });
            });
            t.Start();
        }

        #region Polling

        private void Start_Checking()
        {
            Thread t = new Thread(() =>
            {
                while (Running)
                {
                    Check_ESRecordLib();

                    Thread.Sleep(100);
                }
            });

            t.Start();
        }

        private void Check_ESRecordLib()
        {
            int progress = -1;
            ESRecordState state = ESRecord.ESRecord_GetState(out progress);

            Application.Current.Dispatcher.Invoke(() =>
            {
                switch(state)
                {
                    case ESRecordState.Idle:
                    case ESRecordState.Compiling:
                    case ESRecordState.Preparing:
                        this.LogStatus.Text = state.ToString();
                        break;

                    case ESRecordState.Warmup:
                    case ESRecordState.Recording:
                        this.LogStatus.Text = state.ToString() + " (" + progress.ToString(provider: System.Globalization.NumberFormatInfo.InvariantInfo) + "%)";
                        break;
                }

                lastState = state;
            });
        }

        #endregion

        #region The shitty tables

        private Grid Generate_RPM_Row(Border child1, Border child2)
        {
            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });

            grid.Children.Add(child1);
            grid.Children.Add(child2);
            return grid;
        }

        private void Generate_RPM_Header()
        {
            Border header1 = new(), header2 = new();

            {
                header1.SetValue(Grid.ColumnProperty, 0);
                header2.SetValue(Grid.ColumnProperty, 1);

                header1.BorderBrush = new SolidColorBrush(Colors.White);
                header2.BorderBrush = new SolidColorBrush(Colors.White);

                header1.BorderThickness = new Thickness(0, 0, 1, 1);
                header2.BorderThickness = new Thickness(0, 0, 0, 1);
            }

            TextBlock tb1 = new(), tb2 = new();

            {
                tb1.Text = "RPM";
                tb2.Text = "Frequency";

                tb1.VerticalAlignment = VerticalAlignment.Center;
                tb2.VerticalAlignment = VerticalAlignment.Center;
             
                tb1.HorizontalAlignment = HorizontalAlignment.Center;
                tb2.HorizontalAlignment = HorizontalAlignment.Center;
            }

            header1.Child = tb1;
            header2.Child = tb2;

            Grid g = Generate_RPM_Row(header1, header2);
            g.SetValue(Grid.RowProperty, 0);

            SampleRPMGrid.RowDefinitions.Add(new RowDefinition { Height = new(27) });
            SampleRPMGrid.Children.Add(g);
        }

        private void Generate_RPM_Item(SampleRPM sample)
        {
            Border header1 = new(), header2 = new();

            {
                header1.SetValue(Grid.ColumnProperty, 0);
                header2.SetValue(Grid.ColumnProperty, 1);

                header1.BorderBrush = new SolidColorBrush(Colors.White);
                header2.BorderBrush = new SolidColorBrush(Colors.White);

                header1.BorderThickness = new Thickness(0, 0, 1, 1);
                header2.BorderThickness = new Thickness(0, 0, 0, 1);
            }

            TextBox tb1 = new(), tb2 = new();

            {
                tb1.Text = sample.RPM.ToString(provider: System.Globalization.NumberFormatInfo.InvariantInfo);
                tb2.Text = sample.Frequency.ToString(provider: System.Globalization.NumberFormatInfo.InvariantInfo);

                tb1.TextChanged += (object sender, TextChangedEventArgs e) =>
                {
                    int i = 0;
                    bool success = int.TryParse(tb1.Text, out i);
                    if (!success)
                    {
                        header1.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
                        tb1.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
                    }
                    else
                    {
                        sample.RPM = i;
                        header1.Background = new SolidColorBrush(Colors.Transparent);
                        tb1.Background = new SolidColorBrush(Colors.Transparent);
                    }
                };

                tb2.TextChanged += (object sender, TextChangedEventArgs e) =>
                {
                    int i = 0;
                    bool success = int.TryParse(tb2.Text, out i);
                    if(!success)
                    {
                        header2.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
                        tb2.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
                        tb2.CaretBrush = new SolidColorBrush(Colors.Black);
                    }
                    else
                    {
                        sample.Frequency = i;
                        header2.Background = new SolidColorBrush(Colors.Transparent);
                        tb2.Background = new SolidColorBrush(Colors.Transparent);
                    }
                };

                tb1.VerticalContentAlignment = VerticalAlignment.Center;
                tb2.VerticalContentAlignment = VerticalAlignment.Center;

                tb1.VerticalAlignment = VerticalAlignment.Stretch;
                tb2.VerticalAlignment = VerticalAlignment.Stretch;

                tb1.HorizontalContentAlignment = HorizontalAlignment.Center;
                tb2.HorizontalContentAlignment = HorizontalAlignment.Center;

                tb1.HorizontalAlignment = HorizontalAlignment.Stretch;
                tb2.HorizontalAlignment = HorizontalAlignment.Stretch;

                tb1.Background = new SolidColorBrush(Colors.Transparent);
                tb2.Background = new SolidColorBrush(Colors.Transparent);

                tb1.Foreground = new SolidColorBrush(Colors.White);
                tb2.Foreground = new SolidColorBrush(Colors.White);
                
                tb1.CaretBrush = new SolidColorBrush(Colors.White);
                tb2.CaretBrush = new SolidColorBrush(Colors.White);

                tb1.BorderThickness = new Thickness(0);
                tb2.BorderThickness = new Thickness(0);
            }

            header1.Child = tb1;
            header2.Child = tb2;

            Grid g = Generate_RPM_Row(header1, header2);
            g.SetValue(Grid.RowProperty, SampleRPMGrid.Children.Count);

            SampleRPMGrid.RowDefinitions.Add(new RowDefinition { Height = new(27) });
            SampleRPMGrid.Children.Add(g);
        }

        void Generate_SampleRPMGrid()
        {
            // Clear grid
            SampleRPMGrid.RowDefinitions.Clear();
            SampleRPMGrid.Children.Clear();
            
            // header
            Generate_RPM_Header();

            foreach (var sample in SampleRPMList)
            {
                Generate_RPM_Item(sample);
            }
        }

        private void Generate_Throttle_Header()
        {
            Border header = new();

            {
                header.SetValue(Grid.ColumnProperty, 0);
                header.BorderBrush = new SolidColorBrush(Colors.White);
                header.BorderThickness = new Thickness(0, 1, 0, 1);
            }

            TextBlock tb = new();

            {
                tb.Text = "Throttle";
                tb.VerticalAlignment = VerticalAlignment.Center;
                tb.HorizontalAlignment = HorizontalAlignment.Center;
            }

            header.Child = tb;
            header.SetValue(Grid.RowProperty, 0);

            SampleThrottleGrid.RowDefinitions.Add(new RowDefinition { Height = new(27) });
            SampleThrottleGrid.Children.Add(header);
        }

        private void Generate_Throttle_Item(SampleThrottle sample)
        {
            Border header = new();

            {
                header.SetValue(Grid.ColumnProperty, 0);
                header.BorderBrush = new SolidColorBrush(Colors.White);
                header.BorderThickness = new Thickness(0, 0, 0, 1);
            }

            TextBox tb = new();

            {
                tb.Text = sample.Throttle.ToString(provider: System.Globalization.NumberFormatInfo.InvariantInfo);

                tb.TextChanged += (object sender, TextChangedEventArgs e) =>
                {
                    int i = 0;
                    bool success = int.TryParse(tb.Text, out i);
                    if (!success)
                    {
                        header.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
                        tb.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
                    }
                    else
                    {
                        sample.Throttle = i;
                        header.Background = new SolidColorBrush(Colors.Transparent);
                        tb.Background = new SolidColorBrush(Colors.Transparent);
                    }
                };

                tb.VerticalContentAlignment = VerticalAlignment.Center;
                tb.VerticalAlignment = VerticalAlignment.Stretch;
                tb.HorizontalContentAlignment = HorizontalAlignment.Center;
                tb.HorizontalAlignment = HorizontalAlignment.Stretch;
                tb.Background = new SolidColorBrush(Colors.Transparent);
                tb.Foreground = new SolidColorBrush(Colors.White);
                tb.CaretBrush = new SolidColorBrush(Colors.White);
                tb.BorderThickness = new Thickness(0);
            }

            header.Child = tb;
            header.SetValue(Grid.RowProperty, SampleThrottleGrid.Children.Count);

            SampleThrottleGrid.RowDefinitions.Add(new RowDefinition { Height = new(27) });
            SampleThrottleGrid.Children.Add(header);
        }

        void Generate_SampleThrottleGrid()
        {
            // Clear grid
            SampleThrottleGrid.RowDefinitions.Clear();
            SampleThrottleGrid.Children.Clear();

            // header
            Generate_Throttle_Header();

            foreach (var sample in SampleThrottleList)
            {
                Generate_Throttle_Item(sample);
            }
        }

        #endregion

        #region Persistence

        void Load_Data()
        {
            if (!File.Exists("data.dat"))
                return;

            BinaryReader br = new(File.OpenRead("data.dat"));
            string s = new (br.ReadChars(4)); // header of some sorts
            if(s != "esrd")
            {
                MessageBox.Show("Invalid data file", "Error");
                return;
            }

            int rpmscount = br.ReadInt16();
            int throttlecount = br.ReadInt16();
            SampleLength.Text = br.ReadInt16().ToString();
            WarmupCount.Text = br.ReadInt16().ToString();

            SampleRPMList.Clear();
            for (int i = 0; i < rpmscount; i++)
                SampleRPMList.Add(new SampleRPM{ RPM = br.ReadInt32(), Frequency = br.ReadInt32() });

            SampleThrottleList.Clear();
            for (int i = 0; i < throttlecount; i++)
                SampleThrottleList.Add(new SampleThrottle { Throttle = (int)br.ReadSByte() });

            br.Close();
        }

        void Load_Data_Convert()
        {
            if (!File.Exists("convert.dat"))
                return;

            BinaryReader br = new(File.OpenRead("convert.dat"));
            string s = new(br.ReadChars(4)); // header of some sorts
            if (s != "esrd")
            {
                MessageBox.Show("Invalid data file", "Error");
                return;
            }

            Convert_StaticFriction.Text = br.ReadSingle().ToString(NUM_INFO);
            Convert_DynamicFriction.Text = br.ReadDouble().ToString(NUM_INFO);
            Convert_IdleRPM.Text = br.ReadInt32().ToString(NUM_INFO);
            Convert_MaximumRPM.Text = br.ReadInt32().ToString(NUM_INFO);
            
            if (Convert_StarterSoundSelection != null)
                Convert_StarterSoundSelection.SelectedIndex = br.ReadInt32();
            else
                br.ReadInt32(); // for the future

            br.Close();
        }

        void Save_Data()
        {
            if (!finishedLoading) return;

            if (SampleRPMList == null || SampleThrottleList == null || SampleLength == null || WarmupCount == null)
                return;

            BinaryWriter bw = new(File.OpenWrite("data.dat"));

            bw.Write(Encoding.ASCII.GetBytes("esrd"));
            bw.Write((Int16)SampleRPMList.Count);
            bw.Write((Int16)SampleThrottleList.Count);
            int i = 5; int.TryParse(SampleLength.Text, NUM_INFO, out i);
            bw.Write((Int16)i);
            i = 100; int.TryParse(WarmupCount.Text, NUM_INFO, out i);
            bw.Write((Int16)i);

            foreach (var sample in SampleRPMList)
            {
                bw.Write(sample.RPM);
                bw.Write(sample.Frequency);
            }

            foreach (var sample in SampleThrottleList)
                bw.Write((sbyte)sample.Throttle);

            bw.Flush();
            bw.Close();
        }

        void Save_Data_Convert()
        {
            if (!finishedLoading) return;

            if (Convert_StaticFriction == null || Convert_DynamicFriction == null || Convert_IdleRPM == null || Convert_MaximumRPM == null || Convert_StarterSoundSelection == null)
                return;

            BinaryWriter bw = new(File.OpenWrite("convert.dat"));

            bw.Write(Encoding.ASCII.GetBytes("esrd"));

            float staticFriction = 23;
            double dynamicFriction = 0.023;
            int idleRpm = 900;
            int maxRpm = 7500;
            int starter = 0;
            starter = Convert_StarterSoundSelection.SelectedIndex;

            float.TryParse(Convert_StaticFriction.Text, NUM_INFO, out staticFriction);
            double.TryParse(Convert_DynamicFriction.Text, NUM_INFO, out dynamicFriction);
            int.TryParse(Convert_IdleRPM.Text, NUM_INFO, out idleRpm);
            int.TryParse(Convert_MaximumRPM.Text, NUM_INFO, out maxRpm);

            bw.Write(staticFriction);
            bw.Write(dynamicFriction);
            bw.Write(idleRpm);
            bw.Write(maxRpm);
            bw.Write(starter);

            bw.Flush();
            bw.Close();
        }

        #endregion

        #region Events

        private void WindowDrag_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void X_Button_Click(object sender, RoutedEventArgs e)
        {
            // cleanup etc
            Running = false;
            recording = false;

            Save_Data();
            Save_Data_Convert();

            this.Close();
        }

        private void Minus_Button_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Load_Button_Click(object sender, RoutedEventArgs e)
        {
            Load_Engine();
        }

        private void Add_Throttle_Click(object sender, RoutedEventArgs e)
        {
            var item = new SampleThrottle();
            if(SampleThrottleList.Count == 0)
            {
                item.Throttle = 100;
            }
            else
                item.Throttle = SampleThrottleList.Last().Throttle;

            SampleThrottleList.Add(item); // clone last
            Generate_SampleThrottleGrid();
            Save_Data();
        }

        private void Remove_Throttle_Click(object sender, RoutedEventArgs e)
        {
            SampleThrottleList.Remove(SampleThrottleList.Last()); // pop last
            Generate_SampleThrottleGrid();
            Save_Data();
        }

        private void Add_RPM_Front_Click(object sender, RoutedEventArgs e)
        {
            var item = new SampleRPM();
            if (SampleRPMList.Count == 0)
            {
                item.RPM = 1000;
                item.Frequency = 10000;
            }
            else
            {
                item.RPM = SampleRPMList.First().RPM;
                item.Frequency = SampleRPMList.First().Frequency;
            }

            SampleRPMList.Prepend(item); // clone first
            Generate_SampleRPMGrid();
            Save_Data();
        }

        private void Add_RPM_Click(object sender, RoutedEventArgs e)
        {
            
            var item = new SampleRPM();
            if (SampleRPMList.Count == 0)
            {
                item.RPM = 1000;
                item.Frequency = 10000;
            }
            else
            {
                item.RPM = SampleRPMList.Last().RPM;
                item.Frequency = SampleRPMList.Last().Frequency;
            }

            SampleRPMList.Add(item); // clone last
            Generate_SampleRPMGrid();
            Save_Data();
        }

        private void Remove_RPM_Click(object sender, RoutedEventArgs e)
        {
            SampleRPMList.Remove(SampleRPMList.Last()); // pop last
            Generate_SampleRPMGrid();
            Save_Data();
        }

        private void Start_Rendering_Click(object sender, RoutedEventArgs e)
        {
            if(!recording)
            {
                recording = true;
                Start_Recording();
                StartRecordingButton.Content = "Stop recording";
            }
            else
            {
                recording = false;
                StartRecordingButton.Content = "Start recording";
            }
        }

        private void Generate_Clear_Click(object sender, RoutedEventArgs e)
        {
            SampleRPMList = new();
            Generate_SampleRPMGrid();
            Save_Data();
        }

        private void Generate_RPM_Click(object sender, RoutedEventArgs e)
        {
            SampleRPMList = new();

            int min = 1000, max = 8000, step = 500, freq = 10000;

            bool success = int.TryParse(Generate_MinimumRPM.Text, out min);
            if (!success) { MessageBox.Show("Failed to parse \"" + Generate_MinimumRPM.Text + "\" as integer", "Error"); return; }

            success = int.TryParse(Generate_RPMStep.Text, out step);
            if (!success) { MessageBox.Show("Failed to parse \"" + Generate_RPMStep.Text + "\" as integer", "Error"); return; }
            
            success = int.TryParse(Generate_MaximumRPM.Text, out max);
            if (!success) { MessageBox.Show("Failed to parse \"" + Generate_MaximumRPM.Text + "\" as integer", "Error"); return; }
            
            success = int.TryParse(Generate_Frequency.Text, out freq);
            if (!success) { MessageBox.Show("Failed to parse \"" + Generate_Frequency.Text + "\" as integer", "Error"); return; }

            for (int i = min; i <= max; i += step)
            {
                SampleRPMList.Add(new SampleRPM { RPM = i, Frequency = freq });
            }

            Generate_SampleRPMGrid();
            Save_Data();
        }

        private void Refresh_Engines_Click(object sender, RoutedEventArgs e)
        {
            Load_Recorded_Engines();
        }

        private void Refresh_Sound_Click(object sender, RoutedEventArgs e)
        {
            Load_Starter_Data();
        }

        #endregion

        #region Convert section Events

        private void Convert_StaticFriction_TextChanged(object sender, TextChangedEventArgs e)
        {
            float f = 0;
            if(float.TryParse(Convert_StaticFriction.Text, NUM_INFO, out f))
            {
                Refresh_Convert_Dyno();
                Save_Data_Convert();
                Convert_StaticFriction.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
                Convert_StaticFriction.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
        }

        private void Convert_DynamicFriction_TextChanged(object sender, TextChangedEventArgs e)
        {
            float f = 0;
            if (float.TryParse(Convert_DynamicFriction.Text, NUM_INFO, out f))
            {
                Refresh_Convert_Dyno();
                Save_Data_Convert();
                Convert_DynamicFriction.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
                Convert_DynamicFriction.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
        }

        private void Convert_IdleRPM_TextChanged(object sender, TextChangedEventArgs e)
        {
            int f = 0;
            if (int.TryParse(Convert_IdleRPM.Text, NUM_INFO, out f))
            {
                Refresh_Convert_Dyno();
                Save_Data_Convert();
                Convert_IdleRPM.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
                Convert_IdleRPM.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));
        }

        private void Convert_MaximumRPM_TextChanged(object sender, TextChangedEventArgs e)
        {
            int f = 0;
            if (int.TryParse(Convert_MaximumRPM.Text, NUM_INFO, out f))
            {
                Refresh_Convert_Dyno();
                Save_Data_Convert();
                Convert_MaximumRPM.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
                Convert_MaximumRPM.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x45, 0x45));

        }

        private void Convert_RecordedEngineSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Refresh_Convert_Dyno();
        }

        private void Open_File(string extension)
        {
            if (RecordedEngines.Count == 0 || Convert_RecordedEngineSelection.SelectedIndex == -1)
            {
                MessageBox.Show("No recorded engines, or something went horribly wrong. Try recording an engine");
                return;
            }

            string name = RecordedEngines[Convert_RecordedEngineSelection.SelectedIndex].Name;
            name = Blendify(name);
            if (File.Exists("./exports/" + name + extension))
                Open_In(System.IO.Path.GetFullPath(name));
            else
                MessageBox.Show("File not found. Try converting your engine first.");
        }

        private void Open_Blend_Click(object sender, RoutedEventArgs e)
        {
            Open_File(".sfxBlend2D.json");
        }

        private void Open_JBeam_Click(object sender, RoutedEventArgs e)
        {
            Open_File(".jbeam");
        }

        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            Convert_Recorded_Engine();
        }
        
        #endregion
    }

    public class SampleRPM
    {
        public int RPM {  get; set; }

        public int Frequency { get; set; }
    }

    public class SampleThrottle
    {
        public int Throttle { get; set; }
    }

    public class RecordedEngine
    {
        public string Name = "";
        public float Displacement = 0.0f;
        public int Redline = 0;
        public Dictionary<int, KeyValuePair<float, float>> Dyno0 = new();
        public Dictionary<int, KeyValuePair<float, float>> Dyno100 = new();

        public int _MinRPM = int.MaxValue;
        public int _MaxRPM = int.MinValue;

        public float _MaxTorque = float.MinValue;
        public float _MinTorque = float.MaxValue;

        public static RecordedEngine Load(string name)
        {
            RecordedEngine engine = new();

            bool has0 = false, has100 = false;
            string[] dyno = File.ReadAllLines("./engines/" + name + ".csv");
            for (int i = 1; i < dyno.Length; i++)
            {
                int rpm, throttle;
                float power, torque;
                string[] parts = dyno[i].Split(",");
                rpm = int.Parse(parts[0]);
                throttle = int.Parse(parts[1]);
                power = float.Parse(parts[2], System.Globalization.NumberFormatInfo.InvariantInfo);
                torque = float.Parse(parts[3], System.Globalization.NumberFormatInfo.InvariantInfo);

                if (rpm < engine._MinRPM) engine._MinRPM = rpm;
                if (rpm > engine._MaxRPM) engine._MaxRPM = rpm;

                if (throttle == 0)
                {
                    has0 = true;
                    engine.Dyno0.Add(rpm, new KeyValuePair<float, float>(power, torque));
                }
                else if(throttle == 100)
                {
                    has100 = true;

                    if (torque < engine._MinTorque) engine._MinTorque = torque;
                    if (torque > engine._MaxTorque) engine._MaxTorque = torque;
                    engine.Dyno100.Add(rpm, new KeyValuePair<float, float>(power, torque));
                }
            }

            if(has0 && has100)
            {
                engine.Name = name;
            }
            else
            {
                engine.Name = "-";
            }

            /*string[] files = Directory.GetFiles("./samples/", name + "*.wav");
            foreach (var item in files)
            {
                string filename = System.IO.Path.GetFileNameWithoutExtension(item);
                string[] parts = filename.Split("_");
                int rpm = int.Parse(parts[1]);
                int throttle = int.Parse(parts[2]);

                Console.WriteLine(filename);
            }*/

            return engine;
        }
    }
}