// DBA CNC 1.5
// Opens the CH340 USB serial port and sends Grbl 1.1 jog commands ($J=).
// Each click moves one step. Esc or Stop sends jog-cancel (0x85).
// G91 and G21 on a jog apply to that move only. Grbl does not keep them.
//
// Build: Build.bat
// Run:   "DBA CNC.exe"

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("DBA CNC")]
[assembly: AssemblyProduct("DBA CNC")]
[assembly: AssemblyDescription("Scribe product numbers on aluminum plates.")]
[assembly: AssemblyCompany("DBA")]
[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]

namespace DbaCbc
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), "DBA CNC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm());
        }
    }

    sealed class PortItem
    {
        public string PortName;
        public string Label;
        public bool IsCnc;

        public override string ToString()
        {
            return Label;
        }
    }

    static class PortScanner
    {
        static readonly Regex ComName = new Regex(@"\((COM\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<PortItem> List()
        {
            var map = new Dictionary<string, PortItem>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        try
                        {
                            string name = Convert.ToString(mo["Name"]);
                            string port = ExtractCom(name);
                            if (port == null || map.ContainsKey(port))
                                continue;
                            var item = new PortItem();
                            item.PortName = port;
                            item.Label = name;
                            item.IsCnc = IsController(name);
                            map[port] = item;
                        }
                        finally
                        {
                            mo.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // Device names are optional. Port numbers still work.
            }

            foreach (string raw in SerialPort.GetPortNames())
            {
                if (string.IsNullOrEmpty(raw))
                    continue;
                string port = raw.ToUpperInvariant();
                if (map.ContainsKey(port))
                    continue;
                var item = new PortItem();
                item.PortName = port;
                item.Label = port;
                item.IsCnc = false;
                map[port] = item;
            }

            var list = new List<PortItem>(map.Values);
            list.Sort(delegate(PortItem a, PortItem b)
            {
                int rank = b.IsCnc.CompareTo(a.IsCnc);
                if (rank != 0)
                    return rank;
                return string.Compare(a.PortName, b.PortName, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static string ExtractCom(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            Match match = ComName.Match(name);
            if (!match.Success)
                return null;
            return match.Groups[1].Value.ToUpperInvariant();
        }

        static bool IsController(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.IndexOf("CH340", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("USB-SERIAL", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("wch", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    sealed class GrblLink
    {
        public event Action<string> Line;
        public event Action<string> Fault;

        readonly object _ioLock = new object();
        SerialPort _port;
        Thread _reader;
        volatile bool _run;
        ManualResetEvent _opened;
        string _openError;

        public bool IsOpen
        {
            get
            {
                lock (_ioLock)
                    return _port != null && _port.IsOpen;
            }
        }

        public void Open(string portName, int baud)
        {
            _run = true;
            _openError = null;
            _opened = new ManualResetEvent(false);
            _reader = new Thread(delegate() { OpenAndRead(portName, baud); });
            _reader.IsBackground = true;
            _reader.Name = "grbl-serial";
            _reader.Start();
            _opened.WaitOne(5000);
            if (_openError != null)
                throw new InvalidOperationException(_openError);
            if (!IsOpen)
                throw new InvalidOperationException("Could not open " + portName + ".");
        }

        public void Close()
        {
            Thread reader;
            lock (_ioLock)
            {
                _run = false;
                reader = _reader;
                if (_port != null)
                {
                    try
                    {
                        if (_port.IsOpen)
                        {
                            _port.Write(new byte[] { 0x85 }, 0, 1);
                            _port.Close();
                        }
                    }
                    catch
                    {
                    }
                    try { _port.Dispose(); }
                    catch { }
                    _port = null;
                }
            }
            if (reader != null && Thread.CurrentThread != reader)
                reader.Join(1000);
        }

        public void WriteLine(string command)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(command + "\n");
            lock (_ioLock)
            {
                if (_port == null || !_port.IsOpen)
                    throw new InvalidOperationException("Not connected.");
                _port.Write(bytes, 0, bytes.Length);
            }
        }

        public void WriteRealtime(byte value)
        {
            lock (_ioLock)
            {
                if (_port == null || !_port.IsOpen)
                    throw new InvalidOperationException("Not connected.");
                _port.Write(new byte[] { value }, 0, 1);
            }
        }

        void OpenAndRead(string portName, int baud)
        {
            try
            {
                var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One);
                port.Handshake = Handshake.None;
                port.DtrEnable = true;
                port.RtsEnable = true;
                port.ReadTimeout = 200;
                port.WriteTimeout = 2000;
                port.Encoding = Encoding.ASCII;
                port.NewLine = "\n";
                port.Open();
                lock (_ioLock)
                    _port = port;
            }
            catch (Exception ex)
            {
                _openError = ex.Message;
                _run = false;
                _opened.Set();
                return;
            }

            _opened.Set();
            var pending = new StringBuilder();
            while (_run)
            {
                string chunk = null;
                try
                {
                    lock (_ioLock)
                    {
                        if (!_run || _port == null || !_port.IsOpen)
                            break;
                        if (_port.BytesToRead > 0)
                            chunk = _port.ReadExisting();
                    }
                }
                catch (Exception ex)
                {
                    if (_run)
                    {
                        _run = false;
                        RaiseFault(ex.Message);
                    }
                    break;
                }

                if (chunk == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                pending.Append(chunk);
                ExtractLines(pending);
            }
        }

        void ExtractLines(StringBuilder pending)
        {
            while (true)
            {
                string all = pending.ToString();
                int cut = all.IndexOf('\n');
                if (cut < 0)
                    break;
                string line = all.Substring(0, cut).Trim('\r', ' ', '\t');
                pending.Remove(0, cut + 1);
                if (line.Length == 0)
                    continue;
                RaiseLine(line);
            }
        }

        void RaiseLine(string line)
        {
            Action<string> handler = Line;
            if (handler != null)
                handler(line);
        }

        void RaiseFault(string message)
        {
            Action<string> handler = Fault;
            if (handler != null)
                handler(message);
        }
    }

    sealed class PlotPoint
    {
        public bool Set;
        public double X;
        public double Y;
        public double Z;
    }

    sealed class MainForm : Form
    {
        static readonly Color Ink = Color.FromArgb(18, 18, 18);
        static readonly Color Brand = Color.FromArgb(196, 18, 46);
        const byte JogCancel = 0x85;
        const byte FeedHold = (byte)'!';
        const byte CycleStart = (byte)'~';
        const byte SoftReset = 0x18;
        const double MaxFeed = 1000;
        const double MaxZStep = 2.0;

        static readonly Regex StatusPattern = new Regex(@"^<([^|>]+)(?:\|(.*))?>$", RegexOptions.Compiled);
        static readonly Regex PosPattern = new Regex(@"(MPos|WPos):(-?\d+\.?\d*),(-?\d+\.?\d*),(-?\d+\.?\d*)", RegexOptions.Compiled);

        readonly GrblLink _link = new GrblLink();
        readonly System.Windows.Forms.Timer _statusTimer = new System.Windows.Forms.Timer();
        readonly JogKeyFilter _keyFilter;

        ComboBox _ports;
        ComboBox _baud;
        Button _refresh;
        Button _connect;
        Label _stateLabel;
        Label _firmwareLabel;
        Label _posLabel;
        Label _posMeta;
        Button _unlock;
        Button _resume;
        RadioButton _step01;
        RadioButton _step1;
        RadioButton _step5;
        TextBox _feed;
        Button _yPlus;
        Button _yMinus;
        Button _xPlus;
        Button _xMinus;
        Button _zPlus;
        Button _zMinus;
        Button _stop;
        Button _pauseJob;
        bool _jobPaused;
        bool _jobTimerFrozen;
        TextBox _log;
        Button _plot1Top;
        Button _plot1Bottom;
        Button _plot1Right;
        Button _plot2Top;
        Button _goPlate1;
        Button _goPlate2;
        Button _clearPlate1;
        Button _clearPlate2;
        Label _coord1Top;
        Label _coord1Bottom;
        Label _coord1Right;
        Label _coord2Top;
        Label _coord2Bottom;
        Label _coord2Right;
        Label _leftTitle;
        Label _rightTitle;
        Label _plateSize;
        TextBox _lift;
        Button _setTrueHome;
        Label _trueHomeLabel;
        Button _plot1Z;
        Button _plot2Z;
        readonly bool[] _zSet = new bool[8];
        readonly double[] _zValue = new double[8];
        Label _coord1Z;
        Label _coord2Z;
        Label _plate2Size;
        TextBox _product;
        Label _productLabel;
        readonly string[] _rowProduct = new string[] { "", "", "", "" };
        readonly bool[] _rowProductSeen = new bool[4];
        bool _legacyProductSeen;
        string _legacyProduct = "";
        bool _productQuiet;
        CheckBox _applyAll;
        bool _applyAllQuiet;
        CheckBox _redo;
        NumericUpDown _redoCount;
        NumericUpDown _depth;
        Panel _preview;
        bool _snappingDepth;
        Button _goScribe;
        readonly bool[] _plateOn = new bool[] { true, true, false, false, false, false, false, false };
        NumericUpDown _plateCount;
        bool _plateCountQuiet;
        bool _platesFromFile;
        int _viewRow;
        Label[] _rowTags;
        Label _jobTime;
        Button _goHome;
        Button _loadPlates;
        Button[] _platePick;
        Button[] _setPlot;
        readonly PlotPoint[] _upperSet = new PlotPoint[] {
            new PlotPoint(), new PlotPoint(), new PlotPoint()
        };
        bool _jobTiming;
        DateTime _jobStartedUtc;
        TimeSpan _jobElapsed;
        bool _haveMpos;
        double _mposX;
        double _mposY;
        double _mposZ;
        bool _haveZTravel;
        double _zTravel;
        bool _loadActive;
        bool _workFromMachine;
        double _homeMachineX;
        double _homeMachineY;
        double _homeMachineZ;
        int _statusMask;
        bool _haveStatusMask;

        int _generation;
        bool _connecting;
        bool _closing;
        bool _ready;
        bool _reportInches;
        bool _loggedUnits;
        bool _jogOutstanding;
        bool _baudHintShown;
        DateTime _jogSentUtc;
        string _state = "";
        string _posKind = "";
        readonly List<string> _motionQueue = new List<string>();
        bool _plateMoveActive;
        bool _havePos;
        double _curX;
        double _curY;
        double _curZ;
        readonly PlotPoint _p1Top = new PlotPoint();
        readonly PlotPoint _p1Bottom = new PlotPoint();
        readonly PlotPoint _p1Right = new PlotPoint();
        readonly PlotPoint _p2Top = new PlotPoint();
        readonly PlotPoint _p2Bottom = new PlotPoint();
        readonly PlotPoint _p2Right = new PlotPoint();
        string _fixtureKind = "";
        bool _fixtureInches;
        bool _trueHomeSet;
        bool _homeSet;
        bool _scribeActive;
        bool _p1ZSet;
        bool _p2ZSet;
        double _p1Z;
        double _p2Z;
        bool _haveLast;
        double _lastX;
        double _lastY;
        double _lastZ;
        bool _lastInches;
        bool _suppressLastSave;
        bool _returnHomeStarted;
        bool _returnHomeActive;
        bool _settingTrueHome;
        bool _atTrueHomeOnPurpose;
        bool _unitsSeen;
        DateTime _lastSaveUtc;
        Image _logoImage;
        ComboBox _display;
        bool _displayQuiet;
        float _uiScale = 1f;
        string _savedDisplay = "Fit screen";
        const int BaseWidth = 1260;
        const int BaseHeight = 984;
        const int BaseMinWidth = 1180;
        const int BaseMinHeight = 924;

        public MainForm()
        {
            Text = "DBA CNC 1.5";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.FromArgb(245, 245, 245);
            MinimumSize = new Size(BaseMinWidth, BaseMinHeight);
            Size = new Size(BaseWidth, BaseHeight);
            KeyPreview = true;

            BuildUi();

            _link.Line += delegate(string line) { Ui(delegate { HandleLine(line); }); };
            _link.Fault += delegate(string message) { Ui(delegate { HandleFault(message); }); };

            _statusTimer.Interval = 250;
            _statusTimer.Tick += delegate { PollStatus(); };

            _keyFilter = new JogKeyFilter(this);
            Application.AddMessageFilter(_keyFilter);
            FormClosing += OnFormClosing;
            Load += delegate { OnLoaded(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _keyFilter != null)
                Application.RemoveMessageFilter(_keyFilter);
            if (disposing && _logoImage != null)
                _logoImage.Dispose();
            base.Dispose(disposing);
        }

        void BuildUi()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.Margin = new Padding(0);
            root.Padding = new Padding(0);
            root.BackColor = BackColor;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font("Segoe UI", 10f);
            tabs.Padding = new Point(12, 4);

            var scribePage = new TabPage("Scribe");
            scribePage.BackColor = BackColor;
            scribePage.Padding = new Padding(0);
            scribePage.UseVisualStyleBackColor = false;
            var scribe = new TableLayoutPanel();
            scribe.Dock = DockStyle.Fill;
            scribe.ColumnCount = 1;
            scribe.RowCount = 5;
            scribe.Margin = new Padding(0);
            scribe.BackColor = BackColor;
            scribe.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            scribe.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
            scribe.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            scribe.RowStyles.Add(new RowStyle(SizeType.Absolute, 232));
            scribe.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            scribe.Controls.Add(BuildConnectRow(), 0, 0);
            scribe.Controls.Add(BuildStatus(), 0, 1);
            scribe.Controls.Add(BuildScribe(), 0, 2);
            scribe.Controls.Add(BuildPlates(), 0, 3);
            scribe.Controls.Add(BuildJog(), 0, 4);
            scribePage.Controls.Add(scribe);

            var consolePage = new TabPage("Console");
            consolePage.BackColor = BackColor;
            consolePage.Padding = new Padding(0);
            consolePage.UseVisualStyleBackColor = false;
            consolePage.Controls.Add(BuildLog());

            tabs.TabPages.Add(scribePage);
            tabs.TabPages.Add(consolePage);
            root.Controls.Add(tabs, 0, 1);
        }

        Control BuildHeader()
        {
            var header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Ink;

            _logoImage = LoadLogo();
            int textX = 20;
            if (_logoImage != null)
            {
                var mark = new PictureBox();
                mark.Image = _logoImage;
                mark.SizeMode = PictureBoxSizeMode.Zoom;
                mark.BackColor = Ink;
                mark.Size = new Size(126, 126);
                mark.Location = new Point(10, 8);
                header.Controls.Add(mark);
                textX = 148;
            }

            var title = new Label();
            title.Text = "DBA CNC";
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(textX, 38);

            var subtitle = new Label();
            subtitle.Text = "Version 1.5";
            subtitle.ForeColor = Color.FromArgb(230, 230, 230);
            subtitle.Font = new Font("Segoe UI", 10f);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(textX, 74);

            var displayHost = new Panel();
            displayHost.Dock = DockStyle.Right;
            displayHost.Width = 176;
            displayHost.BackColor = header.BackColor;

            var displayLabel = new Label();
            displayLabel.Text = "Display";
            displayLabel.ForeColor = Color.FromArgb(230, 230, 230);
            displayLabel.Font = new Font("Segoe UI", 9f);
            displayLabel.AutoSize = true;
            displayLabel.Location = new Point(0, 60);

            _display = new ComboBox();
            _display.DropDownStyle = ComboBoxStyle.DropDownList;
            _display.Width = 104;
            _display.Location = new Point(58, 56);
            _display.Items.Add("Fit screen");
            _display.Items.Add("100%");
            _display.Items.Add("90%");
            _display.Items.Add("80%");
            _display.Items.Add("70%");
            _display.Items.Add("60%");
            _display.SelectedIndex = 0;
            _display.SelectedIndexChanged += delegate { OnDisplayChanged(); };

            displayHost.Controls.Add(displayLabel);
            displayHost.Controls.Add(_display);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(displayHost);
            return header;
        }

        Image LoadLogo()
        {
            try
            {
                Stream stream = OpenLogoStream();
                if (stream == null)
                    return null;
                using (stream)
                using (Image raw = Image.FromStream(stream))
                {
                    Bitmap copy = new Bitmap(raw.Width, raw.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (Graphics draw = Graphics.FromImage(copy))
                    {
                        draw.Clear(Color.Transparent);
                        draw.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                    }
                    return copy;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Stream OpenLogoStream()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith("logo.png", StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(names[i]);
            }
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.png");
            if (File.Exists(beside))
                return File.OpenRead(beside);
            string parent = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "logo.png"));
            if (File.Exists(parent))
                return File.OpenRead(parent);
            return null;
        }

        void OnDisplayChanged()
        {
            if (_displayQuiet || _display == null || _display.SelectedItem == null)
                return;
            ApplyUiScale(ScaleFromChoice(_display.SelectedItem.ToString()));
            SaveTemplate();
        }

        void ApplySavedDisplay()
        {
            string choice = _savedDisplay;
            if (choice == null || choice.Length == 0)
                choice = "Fit screen";
            SelectDisplay(choice, false);
        }

        void SelectDisplay(string choice, bool save)
        {
            if (_display == null)
                return;
            int index = 0;
            for (int i = 0; i < _display.Items.Count; i++)
            {
                if (string.Equals(_display.Items[i].ToString(), choice, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            _displayQuiet = true;
            _display.SelectedIndex = index;
            _displayQuiet = false;
            ApplyUiScale(ScaleFromChoice(_display.SelectedItem.ToString()));
            if (save)
                SaveTemplate();
        }

        float ScaleFromChoice(string choice)
        {
            if (choice == "100%") return 1f;
            if (choice == "90%") return 0.9f;
            if (choice == "80%") return 0.8f;
            if (choice == "70%") return 0.7f;
            if (choice == "60%") return 0.6f;
            return FitScale();
        }

        float FitScale()
        {
            Rectangle area = IsHandleCreated
                ? Screen.FromControl(this).WorkingArea
                : Screen.PrimaryScreen.WorkingArea;
            float sx = (area.Width - 12f) / BaseWidth;
            float sy = (area.Height - 12f) / BaseHeight;
            float fit = Math.Min(sx, sy);
            float[] steps = new float[] { 1f, 0.9f, 0.8f, 0.7f, 0.6f };
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] <= fit + 0.01f)
                    return steps[i];
            }
            return 0.6f;
        }

        void ApplyUiScale(float scale)
        {
            if (scale < 0.6f)
                scale = 0.6f;
            if (scale > 1f)
                scale = 1f;
            float factor = scale / _uiScale;
            if (factor < 0.99f || factor > 1.01f)
            {
                MinimumSize = Size.Empty;
                ScaleExplicitFonts(this, factor);
                Font = new Font(Font.FontFamily, Math.Max(6f, Font.Size * factor), Font.Style, Font.Unit);
                Scale(new SizeF(factor, factor));
                _uiScale = scale;
            }
            MinimumSize = new Size(
                (int)Math.Round(BaseMinWidth * scale),
                (int)Math.Round(BaseMinHeight * scale));
            Size = new Size(
                (int)Math.Round(BaseWidth * scale),
                (int)Math.Round(BaseHeight * scale));
            if (Visible)
                MoveFullyOnScreen();
        }

        static void ScaleExplicitFonts(Control control, float factor)
        {
            foreach (Control child in control.Controls)
            {
                if (child.Parent != null && !object.ReferenceEquals(child.Font, child.Parent.Font))
                {
                    float size = child.Font.Size * factor;
                    if (size < 6f)
                        size = 6f;
                    child.Font = new Font(child.Font.FontFamily, size, child.Font.Style, child.Font.Unit);
                }
                ScaleExplicitFonts(child, factor);
            }
        }

        void MoveFullyOnScreen()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int x = Location.X;
            int y = Location.Y;
            if (Width <= area.Width)
            {
                if (x < area.Left)
                    x = area.Left;
                if (x + Width > area.Right)
                    x = area.Right - Width;
            }
            else
                x = area.Left;
            if (Height <= area.Height)
            {
                if (y < area.Top)
                    y = area.Top;
                if (y + Height > area.Bottom)
                    y = area.Bottom - Height;
            }
            else
                y = area.Top;
            Location = new Point(x, y);
        }

        Control BuildConnectRow()
        {
            var row = new FlowLayoutPanel();
            row.Dock = DockStyle.Fill;
            row.Padding = new Padding(16, 16, 16, 8);
            row.WrapContents = false;
            row.BackColor = BackColor;

            row.Controls.Add(FieldLabel("Port"));
            _ports = new ComboBox();
            _ports.DropDownStyle = ComboBoxStyle.DropDownList;
            _ports.Width = 300;
            _ports.Margin = new Padding(0, 4, 8, 0);
            row.Controls.Add(_ports);

            _refresh = new Button();
            _refresh.Text = "Refresh";
            _refresh.AutoSize = true;
            _refresh.Margin = new Padding(0, 3, 16, 0);
            _refresh.Click += delegate { ReloadPorts(); };
            row.Controls.Add(_refresh);

            row.Controls.Add(FieldLabel("Baud"));
            _baud = new ComboBox();
            _baud.DropDownStyle = ComboBoxStyle.DropDownList;
            _baud.Width = 100;
            _baud.Margin = new Padding(0, 4, 12, 0);
            _baud.Items.Add(115200);
            _baud.Items.Add(9600);
            _baud.SelectedIndex = 0;
            row.Controls.Add(_baud);

            _connect = new Button();
            _connect.Text = "Connect";
            _connect.AutoSize = true;
            _connect.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _connect.MinimumSize = new Size(148, 32);
            _connect.Padding = new Padding(14, 4, 14, 4);
            _connect.Margin = new Padding(8, 2, 16, 0);
            StyleButton(_connect, Color.FromArgb(30, 110, 200), 10f);
            _connect.Click += delegate { ToggleConnection(); };
            row.Controls.Add(_connect);
            return row;
        }

        static Label FieldLabel(string text)
        {
            var label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = new Padding(0, 8, 8, 0);
            return label;
        }

        Control BuildStatus()
        {
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, 0, 12, 8);
            host.BackColor = BackColor;

            var card = new TableLayoutPanel();
            card.Dock = DockStyle.Fill;
            card.ColumnCount = 1;
            card.RowCount = 3;
            card.BackColor = Color.White;
            card.Padding = new Padding(16, 8, 16, 8);
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            host.Controls.Add(card);

            var top = new TableLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.ColumnCount = 2;
            top.RowCount = 1;
            top.Margin = new Padding(0);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            _stateLabel = new Label();
            _stateLabel.Text = "Disconnected";
            _stateLabel.Dock = DockStyle.Fill;
            _stateLabel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            _stateLabel.ForeColor = Color.FromArgb(90, 98, 110);
            _stateLabel.TextAlign = ContentAlignment.MiddleLeft;
            top.Controls.Add(_stateLabel, 0, 0);

            _firmwareLabel = new Label();
            _firmwareLabel.Text = "";
            _firmwareLabel.Dock = DockStyle.Fill;
            _firmwareLabel.Font = new Font("Segoe UI", 10f);
            _firmwareLabel.ForeColor = Color.FromArgb(70, 82, 96);
            _firmwareLabel.TextAlign = ContentAlignment.MiddleRight;
            top.Controls.Add(_firmwareLabel, 1, 0);
            card.Controls.Add(top, 0, 0);

            var posRow = new TableLayoutPanel();
            posRow.Dock = DockStyle.Fill;
            posRow.ColumnCount = 2;
            posRow.RowCount = 1;
            posRow.Margin = new Padding(0);
            posRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            posRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));

            _posLabel = new Label();
            _posLabel.Dock = DockStyle.Fill;
            _posLabel.Font = new Font("Consolas", 14f, FontStyle.Bold);
            _posLabel.ForeColor = Ink;
            _posLabel.TextAlign = ContentAlignment.MiddleLeft;
            _posLabel.Text = "X   ---.---     Y   ---.---     Z   ---.---";
            posRow.Controls.Add(_posLabel, 0, 0);

            var actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            actions.Padding = new Padding(0, 4, 0, 0);

            _goScribe = ActionButton("START", Color.FromArgb(22, 150, 64));
            _goScribe.Click += delegate { StartScribe(); };
            _pauseJob = ActionButton("PAUSE", Brand);
            _pauseJob.Click += delegate { SendPause(); };
            _resume = ActionButton("RESUME", Color.FromArgb(240, 186, 18));
            _resume.Click += delegate { SendResume(); };
            _unlock = ActionButton("UNLOCK", Color.FromArgb(168, 168, 168));
            _unlock.Image = LockMark();
            _unlock.TextImageRelation = TextImageRelation.ImageBeforeText;
            _unlock.Click += delegate { SendUnlock(); };
            PaintAction(_goScribe);
            PaintAction(_pauseJob);
            PaintAction(_resume);
            PaintAction(_unlock);
            actions.Controls.Add(_goScribe);
            actions.Controls.Add(_pauseJob);
            actions.Controls.Add(_resume);
            actions.Controls.Add(_unlock);
            posRow.Controls.Add(actions, 1, 0);
            card.Controls.Add(posRow, 0, 1);

            _posMeta = new Label();
            _posMeta.Text = "Position shows up after the controller answers.";
            _posMeta.Dock = DockStyle.Fill;
            _posMeta.TextAlign = ContentAlignment.MiddleLeft;
            _posMeta.ForeColor = Color.FromArgb(70, 70, 70);
            card.Controls.Add(_posMeta, 0, 2);
            return host;
        }

        Button ActionButton(string text, Color color)
        {
            var button = new Button();
            button.Text = text;
            button.Width = 108;
            button.Height = 34;
            button.Margin = new Padding(6, 0, 0, 0);
            button.UseVisualStyleBackColor = false;
            StyleButton(button, color, 9f);
            return button;
        }

        static Bitmap LockMark()
        {
            Bitmap mark = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics draw = Graphics.FromImage(mark))
            {
                draw.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                draw.Clear(Color.Transparent);
                using (Pen shackle = new Pen(Color.FromArgb(30, 30, 30), 1.7f))
                using (SolidBrush body = new SolidBrush(Color.FromArgb(30, 30, 30)))
                using (SolidBrush hole = new SolidBrush(Color.FromArgb(168, 168, 168)))
                {
                    draw.DrawArc(shackle, 4f, 1f, 8f, 8f, 180, 180);
                    draw.FillRectangle(body, 3, 7, 10, 8);
                    draw.FillRectangle(hole, 7, 10, 2, 3);
                }
            }
            return mark;
        }

        static void PaintAction(Button button)
        {
            if (button == null)
                return;
            Color live = button.Tag is Color ? (Color)button.Tag : button.BackColor;
            button.Enabled = true;
            button.BackColor = live;
            button.ForeColor = live.R + live.G + live.B > 420 ? Ink : Color.White;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(
                Math.Min(255, live.R + 20),
                Math.Min(255, live.G + 20),
                Math.Min(255, live.B + 20));
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(live.R * 3 / 4, live.G * 3 / 4, live.B * 3 / 4);
        }

        static Button UtilityButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Width = 112;
            button.Height = 32;
            button.Margin = new Padding(0, 0, 6, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.UseVisualStyleBackColor = false;
            button.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            PaintUtility(button, false);
            return button;
        }

        static void PaintUtility(Button button, bool ready)
        {
            button.Enabled = true;
            button.Tag = ready;
            if (ready)
            {
                button.BackColor = Ink;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Ink;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 48);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 0, 0);
            }
            else
            {
                button.BackColor = Color.FromArgb(214, 214, 214);
                button.ForeColor = Ink;
                button.FlatAppearance.BorderColor = Color.FromArgb(90, 106, 124);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(198, 208, 218);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 192, 204);
            }
        }

        Control BuildScribe()
        {
            var host = new TableLayoutPanel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, 2, 12, 2);
            host.BackColor = BackColor;
            host.ColumnCount = 1;
            host.RowCount = 2;
            host.Margin = new Padding(0);
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var homeRow = new FlowLayoutPanel();
            homeRow.Dock = DockStyle.Fill;
            homeRow.WrapContents = false;
            homeRow.Margin = new Padding(0);

            _setTrueHome = new Button();
            _setTrueHome.Text = "Set Home";
            _setTrueHome.AutoSize = true;
            _setTrueHome.Height = 32;
            _setTrueHome.Margin = new Padding(0, 4, 8, 0);
            _setTrueHome.Enabled = false;
            StyleButton(_setTrueHome, Ink, 9f);
            _setTrueHome.Click += delegate { SetTrueHome(); };
            homeRow.Controls.Add(_setTrueHome);

            _trueHomeLabel = new Label();
            _trueHomeLabel.AutoSize = true;
            _trueHomeLabel.Margin = new Padding(0, 10, 0, 0);
            _trueHomeLabel.Text = "Jog to the template mark, then Set Home.";
            homeRow.Controls.Add(_trueHomeLabel);

            _goHome = new Button();
            _goHome.Text = "Go Home";
            _goHome.Width = 96;
            _goHome.Height = 32;
            _goHome.Margin = new Padding(16, 4, 8, 0);
            _goHome.UseVisualStyleBackColor = false;
            StyleButton(_goHome, Color.FromArgb(30, 110, 200), 9f);
            PaintAction(_goHome);
            _goHome.Click += delegate { GoToWorkHome(); };
            homeRow.Controls.Add(_goHome);

            _loadPlates = new Button();
            _loadPlates.Text = "Load plates";
            _loadPlates.Width = 118;
            _loadPlates.Height = 32;
            _loadPlates.Margin = new Padding(0, 4, 0, 0);
            _loadPlates.UseVisualStyleBackColor = false;
            StyleButton(_loadPlates, Color.FromArgb(214, 112, 18), 9f);
            PaintAction(_loadPlates);
            _loadPlates.Click += delegate { LoadPlates(); };
            homeRow.Controls.Add(_loadPlates);
            host.Controls.Add(homeRow, 0, 0);

            var jobRow = new FlowLayoutPanel();
            jobRow.Dock = DockStyle.Fill;
            jobRow.WrapContents = false;
            jobRow.Margin = new Padding(0);

            _productLabel = FieldLabel("Row 1");
            jobRow.Controls.Add(_productLabel);
            _product = new TextBox();
            _product.Width = 136;
            _product.MaxLength = 24;
            _product.Margin = new Padding(0, 6, 8, 0);
            _product.TextChanged += delegate { OnProductEdited(); };
            jobRow.Controls.Add(_product);

            _applyAll = new CheckBox();
            _applyAll.Text = "Apply to all";
            _applyAll.AutoSize = true;
            _applyAll.Margin = new Padding(0, 8, 12, 0);
            _applyAll.CheckedChanged += delegate { OnApplyAllChanged(); };
            jobRow.Controls.Add(_applyAll);

            _redo = new CheckBox();
            _redo.Text = "Re-do last";
            _redo.AutoSize = true;
            _redo.Margin = new Padding(0, 8, 4, 0);
            _redo.CheckedChanged += delegate { OnRedoChanged(); };
            jobRow.Controls.Add(_redo);
            _redoCount = new NumericUpDown();
            _redoCount.Minimum = 1;
            _redoCount.Maximum = 24;
            _redoCount.Value = 3;
            _redoCount.Width = 46;
            _redoCount.Enabled = false;
            _redoCount.TextAlign = HorizontalAlignment.Right;
            _redoCount.Margin = new Padding(0, 6, 12, 0);
            _redoCount.ValueChanged += delegate { RefreshPreview(); };
            jobRow.Controls.Add(_redoCount);

            jobRow.Controls.Add(FieldLabel("Depth"));
            _depth = new NumericUpDown();
            _depth.Minimum = 0.0025m;
            _depth.Maximum = 0.01m;
            _depth.Increment = 0.0025m;
            _depth.DecimalPlaces = 4;
            _depth.Value = 0.0050m;
            _depth.Width = 78;
            _depth.TextAlign = HorizontalAlignment.Right;
            _depth.Margin = new Padding(0, 6, 4, 0);
            _depth.ValueChanged += delegate { OnDepthChanged(); };
            _depth.Validating += delegate { SnapDepth(); };
            jobRow.Controls.Add(_depth);
            var depthUnit = new Label();
            depthUnit.Text = "mm";
            depthUnit.AutoSize = true;
            depthUnit.Margin = new Padding(0, 10, 16, 0);
            jobRow.Controls.Add(depthUnit);

            var liftLabel = new Label();
            liftLabel.Text = "Lift";
            liftLabel.AutoSize = true;
            liftLabel.Margin = new Padding(0, 10, 6, 0);
            _lift = new TextBox();
            _lift.Text = "5";
            _lift.Width = 40;
            _lift.Margin = new Padding(0, 6, 4, 0);
            var liftUnit = new Label();
            liftUnit.Text = "mm";
            liftUnit.AutoSize = true;
            liftUnit.Margin = new Padding(0, 10, 16, 0);
            jobRow.Controls.Add(liftLabel);
            jobRow.Controls.Add(_lift);
            jobRow.Controls.Add(liftUnit);

            Button export = new Button();
            export.Text = "Export";
            export.Width = 72;
            export.Height = 32;
            export.Margin = new Padding(0, 4, 6, 0);
            StyleButton(export, Ink, 9f);
            export.Click += delegate { ExportSettings(); };
            jobRow.Controls.Add(export);

            Button import = new Button();
            import.Text = "Import";
            import.Width = 72;
            import.Height = 32;
            import.Margin = new Padding(0, 4, 12, 0);
            StyleButton(import, Ink, 9f);
            import.Click += delegate { ImportSettings(); };
            jobRow.Controls.Add(import);

            _jobTime = new Label();
            _jobTime.Text = "0:00";
            _jobTime.AutoSize = true;
            _jobTime.Font = new Font("Consolas", 12f, FontStyle.Bold);
            _jobTime.Margin = new Padding(0, 8, 12, 0);
            _jobTime.ForeColor = Color.FromArgb(24, 32, 44);
            jobRow.Controls.Add(_jobTime);

            host.Controls.Add(jobRow, 0, 1);
            return host;
        }

        Control BuildPlates()
        {
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, 0, 12, 6);
            host.BackColor = BackColor;

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 3;
            grid.RowCount = 1;
            grid.Margin = new Padding(0);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
            host.Controls.Add(grid);
            grid.Controls.Add(BuildPlateCard(1), 0, 0);
            grid.Controls.Add(BuildPlateCard(2), 1, 0);
            grid.Controls.Add(BuildPlatePicker(), 2, 0);
            return host;
        }

        Control BuildPlatePicker()
        {
            var card = new TableLayoutPanel();
            card.Dock = DockStyle.Fill;
            card.BackColor = Color.White;
            card.Padding = new Padding(6, 4, 6, 4);
            card.Margin = new Padding(6, 0, 0, 0);
            card.ColumnCount = 1;
            card.RowCount = 5;
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            for (int i = 0; i < 4; i++)
                card.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

            var head = new FlowLayoutPanel();
            head.Dock = DockStyle.Fill;
            head.WrapContents = false;
            head.Margin = new Padding(0);
            var platesLabel = new Label();
            platesLabel.Text = "Plates";
            platesLabel.AutoSize = true;
            platesLabel.Margin = new Padding(0, 6, 4, 0);
            _plateCount = new NumericUpDown();
            _plateCount.Minimum = 0;
            _plateCount.Maximum = 8;
            _plateCount.Value = 2;
            _plateCount.Width = 42;
            _plateCount.Margin = new Padding(0, 2, 8, 0);
            _plateCount.ValueChanged += delegate { OnPlateCountChanged(); };
            var hint = new Label();
            hint.Text = "Red = engrave";
            hint.AutoSize = true;
            hint.Margin = new Padding(0, 6, 0, 0);
            hint.ForeColor = Color.FromArgb(70, 80, 92);
            hint.Font = new Font("Segoe UI", 8f);
            head.Controls.Add(platesLabel);
            head.Controls.Add(_plateCount);
            head.Controls.Add(hint);
            card.Controls.Add(head, 0, 0);

            _platePick = new Button[8];
            _setPlot = new Button[3];
            _rowTags = new Label[4];
            for (int visual = 0; visual < 4; visual++)
            {
                int setRow = 3 - visual;
                var line = new TableLayoutPanel();
                line.Dock = DockStyle.Fill;
                line.ColumnCount = 4;
                line.RowCount = 1;
                line.Margin = new Padding(0);
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
                line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var tag = new Label();
                tag.Text = "#" + (setRow + 1).ToString(CultureInfo.InvariantCulture);
                tag.Dock = DockStyle.Fill;
                tag.TextAlign = ContentAlignment.MiddleCenter;
                tag.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
                tag.Cursor = Cursors.Hand;
                tag.BackColor = Color.White;
                int rowNumber = setRow;
                tag.Click += delegate { ViewRow(rowNumber); };
                _rowTags[setRow] = tag;

                Button leftTile = SideTile("L", setRow, true);
                Button rightTile = SideTile("R", setRow, false);
                _platePick[setRow * 2] = leftTile;
                _platePick[setRow * 2 + 1] = rightTile;
                line.Controls.Add(tag, 0, 0);
                line.Controls.Add(leftTile, 1, 0);
                line.Controls.Add(rightTile, 2, 0);
                if (setRow > 0)
                {
                    Button plot = new Button();
                    plot.Text = "Top left";
                    plot.Dock = DockStyle.Fill;
                    plot.Margin = new Padding(4, 3, 0, 3);
                    plot.FlatStyle = FlatStyle.Flat;
                    plot.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
                    int plotRow = setRow;
                    plot.Click += delegate { PlotUpperSet(plotRow); };
                    _setPlot[setRow - 1] = plot;
                    line.Controls.Add(plot, 3, 0);
                }
                card.Controls.Add(line, 0, visual + 1);
            }
            RefreshPlateGrid();
            return card;
        }

        Button SideTile(string text, int row, bool left)
        {
            var button = new Button();
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(2);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            int index = row * 2 + (left ? 0 : 1);
            button.Click += delegate { TogglePlate(index); };
            return button;
        }

        void OnPlateCountChanged()
        {
            if (_plateCountQuiet || _plateCount == null)
                return;
            int count = (int)_plateCount.Value;
            for (int i = 0; i < _plateOn.Length; i++)
                _plateOn[i] = i < count;
            RefreshPlateLabels();
            SaveTemplate();
        }

        void SyncPlateCountBox()
        {
            if (_plateCount == null)
                return;
            int count = 0;
            for (int i = 0; i < _plateOn.Length; i++)
            {
                if (_plateOn[i])
                    count++;
            }
            if (_plateCount.Value == count)
                return;
            _plateCountQuiet = true;
            _plateCount.Value = count;
            _plateCountQuiet = false;
        }

        void TogglePlate(int index)
        {
            if (index < 0 || index >= _plateOn.Length)
                return;
            ViewRow(index / 2);
            _plateOn[index] = !_plateOn[index];
            SyncPlateCountBox();
            RefreshPlateLabels();
            RefreshPlateGrid();
            SaveTemplate();
        }

        void ViewRow(int row)
        {
            if (row < 0 || row > 3)
                return;
            FlushViewedProduct();
            _viewRow = row;
            ShowViewedProduct();
            if (_productLabel != null)
                _productLabel.Text = "Row " + (row + 1).ToString(CultureInfo.InvariantCulture);
            RefreshPlateLabels();
            RefreshPlateGrid();
        }

        void FlushViewedProduct()
        {
            if (_product == null || _viewRow < 0 || _viewRow > 3)
                return;
            string text = _product.Text ?? "";
            if (_applyAll != null && _applyAll.Checked)
            {
                for (int i = 0; i < _rowProduct.Length; i++)
                    _rowProduct[i] = text;
            }
            else
                _rowProduct[_viewRow] = text;
        }

        void OnApplyAllChanged()
        {
            if (_applyAllQuiet)
                return;
            if (_applyAll != null && _applyAll.Checked)
                FlushViewedProduct();
            RefreshPreview();
            SaveTemplate();
        }

        void ShowViewedProduct()
        {
            if (_product == null || _viewRow < 0 || _viewRow > 3)
                return;
            _productQuiet = true;
            _product.Text = _rowProduct[_viewRow] ?? "";
            _productQuiet = false;
            RefreshPreview();
        }

        void OnProductEdited()
        {
            if (_productQuiet)
                return;
            FlushViewedProduct();
            RefreshPreview();
        }

        List<int> SelectedPlates()
        {
            var plates = new List<int>();
            for (int i = 0; i < _plateOn.Length; i++)
            {
                if (_plateOn[i])
                    plates.Add(i + 1);
            }
            return plates;
        }

        static string PlateLabel(int plateNumber)
        {
            int row = (plateNumber - 1) / 2 + 1;
            string side = plateNumber % 2 == 1 ? "L" : "R";
            return "row " + row.ToString(CultureInfo.InvariantCulture) + " " + side;
        }

        string SelectedPlateText()
        {
            var text = new StringBuilder();
            List<int> plates = SelectedPlates();
            for (int i = 0; i < plates.Count; i++)
            {
                if (text.Length > 0)
                    text.Append(", ");
                text.Append(PlateLabel(plates[i]));
            }
            return text.ToString();
        }

        void RefreshPlateGrid()
        {
            if (_platePick == null)
                return;
            for (int i = 0; i < _platePick.Length; i++)
            {
                bool on = _plateOn[i];
                Button tile = _platePick[i];
                if (tile == null)
                    continue;
                tile.FlatAppearance.BorderSize = _viewRow == i / 2 ? 2 : 1;
                if (on)
                {
                    tile.BackColor = Brand;
                    tile.ForeColor = Color.White;
                    tile.FlatAppearance.BorderColor = Color.FromArgb(120, 8, 24);
                    tile.FlatAppearance.MouseOverBackColor = Color.FromArgb(214, 40, 66);
                }
                else
                {
                    tile.BackColor = Color.White;
                    tile.ForeColor = Ink;
                    tile.FlatAppearance.BorderColor = Color.FromArgb(160, 170, 182);
                    tile.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 240, 244);
                }
            }
            if (_rowTags != null)
            {
                for (int i = 0; i < _rowTags.Length; i++)
                {
                    if (_rowTags[i] == null)
                        continue;
                    bool viewed = _viewRow == i;
                    _rowTags[i].BackColor = viewed ? Ink : Color.White;
                    _rowTags[i].ForeColor = viewed ? Color.White : Ink;
                }
            }
            if (_setPlot == null)
                return;
            for (int i = 0; i < _setPlot.Length; i++)
            {
                if (_setPlot[i] == null)
                    continue;
                bool plotted = _upperSet[i].Set;
                _setPlot[i].Text = plotted ? "OK" : "Top left";
                _setPlot[i].BackColor = plotted ? Color.FromArgb(230, 230, 230) : Color.White;
                _setPlot[i].ForeColor = Color.FromArgb(24, 32, 44);
                _setPlot[i].FlatAppearance.BorderColor = Color.FromArgb(120, 132, 148);
            }
        }

        Control BuildPlateCard(int plate)
        {
            bool left = plate == 1;
            string name = left ? "Left plate" : "Right plate";
            var card = new TableLayoutPanel();
            card.Dock = DockStyle.Fill;
            card.BackColor = Color.White;
            card.Padding = new Padding(10, 4, 10, 4);
            card.Margin = new Padding(left ? 0 : 6, 0, left ? 6 : 0, 0);
            card.ColumnCount = 2;
            card.RowCount = 6;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++)
                card.RowStyles.Add(new RowStyle(SizeType.Percent, 16.6f));

            var title = new Label();
            title.Text = name;
            title.Dock = DockStyle.Fill;
            title.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            title.TextAlign = ContentAlignment.MiddleLeft;
            if (left)
                _leftTitle = title;
            else
                _rightTitle = title;
            card.Controls.Add(title, 0, 0);

            var size = new Label();
            size.Dock = DockStyle.Fill;
            size.TextAlign = ContentAlignment.MiddleLeft;
            size.ForeColor = Color.FromArgb(70, 80, 92);
            size.Text = left
                ? "Plot top-left, bottom-left, right edge, and Z."
                : "Plot the top-left and this plate's Z. Size copies from the left.";
            if (left)
                _plateSize = size;
            else
                _plate2Size = size;
            card.Controls.Add(size, 1, 0);

            Button top = PlateButton("Plot top-left");
            Label topCoord = CoordLabel();
            top.Click += delegate { PlotViewedTop(left); };
            AddPlotRow(card, 1, top, topCoord);

            Label bottomCoord = CoordLabel();
            Label rightCoord = CoordLabel();
            Label zeeCoord = CoordLabel();
            Button bottom = null;
            Button right = null;
            Button zee = null;
            if (left)
            {
                bottom = PlateButton("Plot bottom-left");
                bottom.Click += delegate { PlotViewedBase("bottom-left"); };
                AddPlotRow(card, 2, bottom, bottomCoord);

                right = PlateButton("Plot right edge");
                right.Click += delegate { PlotViewedBase("right edge"); };
                AddPlotRow(card, 3, right, rightCoord);

                zee = PlateButton("Plot Z surface");
                zee.Click += delegate { PlotViewedSurface(true); };
                AddPlotRow(card, 4, zee, zeeCoord);
            }
            else
            {
                AddCopiedRow(card, 2, "Bottom-left", bottomCoord);
                AddCopiedRow(card, 3, "Right edge", rightCoord);
                zee = PlateButton("Plot Z surface");
                zee.Click += delegate { PlotViewedSurface(false); };
                AddPlotRow(card, 4, zee, zeeCoord);
            }

            var actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            Button go = PlateButton(left ? "Go to Left" : "Go to Right");
            go.Width = 120;
            int plateNumber = plate;
            go.Click += delegate { GoToPlate(plateNumber); };
            Button clear = PlateButton("Clear");
            clear.Width = 70;
            clear.Enabled = true;
            clear.Click += delegate { ClearPlate(plateNumber); };
            actions.Controls.Add(go);
            actions.Controls.Add(clear);
            card.Controls.Add(actions, 0, 5);
            card.SetColumnSpan(actions, 2);

            if (left)
            {
                _plot1Top = top;
                _plot1Bottom = bottom;
                _plot1Right = right;
                _plot1Z = zee;
                _coord1Top = topCoord;
                _coord1Bottom = bottomCoord;
                _coord1Right = rightCoord;
                _coord1Z = zeeCoord;
                _goPlate1 = go;
                _clearPlate1 = clear;
            }
            else
            {
                _plot2Top = top;
                _plot2Z = zee;
                _coord2Top = topCoord;
                _coord2Bottom = bottomCoord;
                _coord2Right = rightCoord;
                _coord2Z = zeeCoord;
                _goPlate2 = go;
                _clearPlate2 = clear;
            }
            return card;
        }

        static Button PlateButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Height = 28;
            button.Width = 140;
            button.Margin = new Padding(0, 2, 6, 2);
            button.Enabled = false;
            return button;
        }

        static Label CoordLabel()
        {
            var label = new Label();
            label.Text = "not set";
            label.Dock = DockStyle.Fill;
            label.Font = new Font("Consolas", 9.5f);
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        static void AddPlotRow(TableLayoutPanel grid, int row, Button button, Label coord)
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 2, 8, 2);
            grid.Controls.Add(button, 0, row);
            grid.Controls.Add(coord, 1, row);
        }

        static void AddCopiedRow(TableLayoutPanel grid, int row, string caption, Label coord)
        {
            var label = new Label();
            label.Text = caption;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(90, 100, 112);
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(coord, 1, row);
        }

        Control BuildJog()
        {
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, 0, 12, 8);
            host.BackColor = BackColor;

            var pad = new TableLayoutPanel();
            pad.Dock = DockStyle.Fill;
            pad.BackColor = Color.White;
            pad.Padding = new Padding(8, 6, 8, 6);
            pad.ColumnCount = 2;
            pad.RowCount = 1;
            pad.Margin = new Padding(0);
            pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pad.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 620));

            var steps = new FlowLayoutPanel();
            steps.Dock = DockStyle.Top;
            steps.Height = 32;
            steps.WrapContents = false;
            steps.Margin = new Padding(0);
            steps.Controls.Add(FieldLabel("Step"));
            _step01 = StepRadio("0.1 mm", false);
            _step1 = StepRadio("1 mm", true);
            _step5 = StepRadio("5 mm", false);
            steps.Controls.Add(_step01);
            steps.Controls.Add(_step1);
            steps.Controls.Add(_step5);
            steps.Controls.Add(FieldLabel("Feed"));
            _feed = new TextBox();
            _feed.Text = "300";
            _feed.Width = 70;
            _feed.Margin = new Padding(0, 6, 6, 0);
            steps.Controls.Add(_feed);
            steps.Controls.Add(FieldLabel("mm/min"));

            var left = new Panel();
            left.Dock = DockStyle.Fill;
            left.Margin = new Padding(0);

            var body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Margin = new Padding(0);

            var buttons = new TableLayoutPanel();
            buttons.Dock = DockStyle.Left;
            buttons.Width = 380;
            buttons.Margin = new Padding(0);
            buttons.ColumnCount = 5;
            buttons.RowCount = 3;
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
            buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));

            _yPlus = JogButton("Y +");
            _xMinus = JogButton("X -");
            _stop = JogButton("STOP");
            _xPlus = JogButton("X +");
            _yMinus = JogButton("Y -");
            _zPlus = JogButton("Z +");
            _zMinus = JogButton("Z -");

            StyleButton(_yPlus, Ink, 11f);
            StyleButton(_xMinus, Ink, 11f);
            StyleButton(_xPlus, Ink, 11f);
            StyleButton(_yMinus, Ink, 11f);
            StyleButton(_zPlus, Ink, 11f);
            StyleButton(_zMinus, Ink, 11f);
            StyleButton(_stop, Brand, 11f);

            buttons.Controls.Add(_yPlus, 1, 0);
            buttons.Controls.Add(_xMinus, 0, 1);
            buttons.Controls.Add(_stop, 1, 1);
            buttons.Controls.Add(_xPlus, 2, 1);
            buttons.Controls.Add(_yMinus, 1, 2);
            buttons.Controls.Add(_zPlus, 4, 0);
            buttons.Controls.Add(_zMinus, 4, 1);

            WireJog(_yPlus, "Y", 1);
            WireJog(_yMinus, "Y", -1);
            WireJog(_xPlus, "X", 1);
            WireJog(_xMinus, "X", -1);
            WireJog(_zPlus, "Z", 1);
            WireJog(_zMinus, "Z", -1);
            _stop.MouseDown += delegate { SendStop(); };

            body.Controls.Add(buttons);
            left.Controls.Add(body);
            left.Controls.Add(steps);
            pad.Controls.Add(left, 0, 0);

            _preview = new PreviewPanel();
            _preview.Dock = DockStyle.Fill;
            _preview.Margin = new Padding(10, 2, 2, 2);
            _preview.Paint += PaintPreview;
            pad.Controls.Add(_preview, 1, 0);
            host.Controls.Add(pad);
            return host;
        }

        RadioButton StepRadio(string text, bool selected)
        {
            var radio = new RadioButton();
            radio.Text = text;
            radio.AutoSize = true;
            radio.Margin = new Padding(0, 8, 16, 0);
            radio.Checked = selected;
            return radio;
        }

        static Button JogButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3);
            button.Enabled = false;
            button.TabStop = false;
            return button;
        }

        Button ChoiceButton(string text, int width)
        {
            var button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 32;
            button.Margin = new Padding(0, 6, 6, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            button.Tag = false;
            PaintChoice(button, false);
            return button;
        }

        static bool ChoiceOn(Button button)
        {
            return button.Tag is bool && (bool)button.Tag;
        }

        void SelectOne(Button chosen, Button[] group, bool allowNone)
        {
            bool turningOff = allowNone && ChoiceOn(chosen);
            for (int i = 0; i < group.Length; i++)
            {
                bool on = group[i] == chosen && !turningOff;
                group[i].Tag = on;
                PaintChoice(group[i], on);
            }
        }

        static void PaintChoice(Button button, bool on)
        {
            if (on)
            {
                Color live = Color.FromArgb(36, 72, 112);
                button.BackColor = live;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = live;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 96, 146);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(24, 52, 84);
            }
            else
            {
                button.BackColor = Color.White;
                button.ForeColor = Color.FromArgb(36, 48, 64);
                button.FlatAppearance.BorderColor = Color.FromArgb(176, 186, 198);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(228, 234, 242);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(210, 218, 228);
            }
        }

        static void StyleButton(Button button, Color back, float size)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = back;
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI", size, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            int r = Math.Min(255, back.R + 28);
            int g = Math.Min(255, back.G + 28);
            int b = Math.Min(255, back.B + 28);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(r, g, b);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(back.R * 3 / 4, back.G * 3 / 4, back.B * 3 / 4);
            button.Tag = back;
        }

        void PaintLive(Button button, bool enabled)
        {
            Color live = button.Tag is Color ? (Color)button.Tag : button.BackColor;
            button.Enabled = enabled;
            if (enabled)
            {
                button.BackColor = live;
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(
                    Math.Min(255, live.R + 28),
                    Math.Min(255, live.G + 28),
                    Math.Min(255, live.B + 28));
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(live.R * 3 / 4, live.G * 3 / 4, live.B * 3 / 4);
            }
            else
            {
                Color quiet = Color.FromArgb(206, 210, 216);
                button.BackColor = quiet;
                button.ForeColor = Color.FromArgb(120, 128, 138);
                button.FlatAppearance.MouseOverBackColor = quiet;
                button.FlatAppearance.MouseDownBackColor = quiet;
            }
        }

        void PaintJobButton(Button button, bool ready)
        {
            if (button == null)
                return;
            Color live = button.Tag is Color ? (Color)button.Tag : button.BackColor;
            button.Enabled = true;
            if (ready)
            {
                button.BackColor = live;
                button.ForeColor = Color.White;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(
                    Math.Min(255, live.R + 28),
                    Math.Min(255, live.G + 28),
                    Math.Min(255, live.B + 28));
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(live.R * 3 / 4, live.G * 3 / 4, live.B * 3 / 4);
            }
            else
            {
                button.BackColor = Color.FromArgb(214, 222, 230);
                button.ForeColor = Color.FromArgb(40, 48, 58);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(198, 208, 218);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 192, 204);
            }
        }

        void WireJog(Button button, string axis, int sign)
        {
            button.Click += delegate { SendJog(axis, sign); };
        }

        Control BuildLog()
        {
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(12, 8, 12, 8);
            host.BackColor = BackColor;

            _log = new TextBox();
            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.White;
            _log.Font = new Font("Consolas", 9f);
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.TabStop = false;

            host.Controls.Add(_log);
            return host;
        }

        void OnLoaded()
        {
            AppendLog("Select USB-SERIAL CH340 and press Connect.");
            AppendLog("Connecting resets most of these boards. Do it while the machine is idle.");
            AppendLog("The machine stays still until you click a jog button or Go to Plate.");
            ReloadPorts();
            LoadProductCatalog();
            LoadTemplate();
            ApplySavedDisplay();
            UpdateActions();
        }

        void ReloadPorts()
        {
            string previous = null;
            PortItem selected = _ports.SelectedItem as PortItem;
            if (selected != null)
                previous = selected.PortName;

            _ports.Items.Clear();
            List<PortItem> ports = PortScanner.List();
            int index = -1;
            for (int i = 0; i < ports.Count; i++)
            {
                _ports.Items.Add(ports[i]);
                if (previous != null && string.Equals(ports[i].PortName, previous, StringComparison.OrdinalIgnoreCase))
                    index = i;
                else if (index < 0 && previous == null && ports[i].IsCnc)
                    index = i;
            }
            if (_ports.Items.Count == 0)
            {
                AppendLog("No serial ports found. Plug in the CNC and press Refresh.");
                return;
            }
            if (index < 0)
                index = 0;
            _ports.SelectedIndex = index;
            PortItem chosen = _ports.SelectedItem as PortItem;
            if (chosen != null && chosen.IsCnc && previous == null)
                AppendLog("Found " + chosen.Label + ".");
        }

        void ToggleConnection()
        {
            if (_connecting)
                return;
            if (_link.IsOpen)
                Disconnect("Disconnected.");
            else
                Connect();
        }

        void Connect()
        {
            PortItem item = _ports.SelectedItem as PortItem;
            if (item == null)
            {
                MessageBox.Show(this, "Select the CH340 port first. It was COM4 in Device Manager.", "DBA CNC", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int baud = (int)_baud.SelectedItem;
            string portName = item.PortName;
            string label = item.Label;
            _connecting = true;
            _connect.Enabled = false;
            _refresh.Enabled = false;
            _ports.Enabled = false;
            _baud.Enabled = false;
            _stateLabel.Text = "Connecting";
            AppendLog("Opening " + label + " at " + baud.ToString(CultureInfo.InvariantCulture) + "...");
            int generation = _generation;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _link.Open(portName, baud);
                    Ui(delegate
                    {
                        if (generation != _generation || IsDisposed)
                            return;
                        OnConnected(label, baud);
                    });
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    Ui(delegate
                    {
                        if (generation != _generation || IsDisposed)
                            return;
                        _connecting = false;
                        ApplyDisconnectedUi();
                        AppendLog("Could not open " + portName + ": " + message);
                        MessageBox.Show(this,
                            "Could not open " + portName + ".\r\n\r\nClose any other program using the CNC (Easel, Candle, UGS), then try again.\r\n\r\n" + message,
                            "DBA CNC",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    });
                }
            });
        }

        void OnConnected(string label, int baud)
        {
            _connecting = false;
            _ready = false;
            _reportInches = false;
            _loggedUnits = false;
            _baudHintShown = false;
            _state = "";
            _jogOutstanding = false;
            _returnHomeStarted = false;
            _returnHomeActive = false;
            _settingTrueHome = false;
            _atTrueHomeOnPurpose = false;
            _unitsSeen = false;
            _suppressLastSave = false;
            _connect.Text = "Disconnect";
            StyleButton(_connect, Ink, 10f);
            _connect.Enabled = true;
            _stateLabel.Text = "Waiting";
            _stateLabel.ForeColor = Color.FromArgb(20, 90, 160);
            _firmwareLabel.Text = "";
            AppendLog("Port open. Waiting for the controller.");
            _statusTimer.Start();
            PollStatus();

            int generation = _generation;
            var wake = new System.Windows.Forms.Timer();
            wake.Interval = 1200;
            wake.Tick += delegate
            {
                wake.Stop();
                wake.Dispose();
                if (generation != _generation || !_link.IsOpen)
                    return;
                try
                {
                    _link.WriteLine("$I");
                    _link.WriteLine("$$");
                }
                catch (Exception ex)
                {
                    AppendLog("Could not query the controller: " + ex.Message);
                }
            };
            wake.Start();

            var giveUp = new System.Windows.Forms.Timer();
            giveUp.Interval = 4000;
            giveUp.Tick += delegate
            {
                giveUp.Stop();
                giveUp.Dispose();
                if (generation != _generation || !_link.IsOpen || _ready || _baudHintShown)
                    return;
                _baudHintShown = true;
                _unitsSeen = true;
                AppendLog("No answer yet. This CH340 board is usually 115200. If it stays quiet, disconnect and try 9600.");
            };
            giveUp.Start();
            UpdateActions();
        }

        void Disconnect(string reason)
        {
            if (_closing)
                return;
            _closing = true;
            try
            {
                _generation++;
                _statusTimer.Stop();
                RememberPosition(true);
                _holdingClear();
                try
                {
                    if (_link.IsOpen)
                        _link.Close();
                }
                catch
                {
                }
                ApplyDisconnectedUi();
                if (!string.IsNullOrEmpty(reason))
                    AppendLog(reason);
            }
            finally
            {
                _closing = false;
            }
        }

        void _holdingClear()
        {
            _jogOutstanding = false;
            _ready = false;
            _state = "";
            _havePos = false;
            _motionQueue.Clear();
            _plateMoveActive = false;
        }

        void ApplyDisconnectedUi()
        {
            _connecting = false;
            _connect.Text = "Connect";
            StyleButton(_connect, Color.FromArgb(30, 110, 200), 10f);
            _connect.Enabled = true;
            _refresh.Enabled = true;
            _ports.Enabled = true;
            _baud.Enabled = true;
            _stateLabel.Text = "Disconnected";
            _stateLabel.ForeColor = Color.FromArgb(90, 98, 110);
            _firmwareLabel.Text = "";
            _posLabel.Text = "X   ---.---     Y   ---.---     Z   ---.---";
            _posMeta.Text = "Position shows up after the controller answers.";
            _posKind = "";
            UpdateActions();
        }

        void StartJobTimer()
        {
            _jobStartedUtc = DateTime.UtcNow;
            _jobElapsed = TimeSpan.Zero;
            _jobTiming = true;
            ShowJobTime();
        }

        void StopJobTimer()
        {
            if (!_jobTiming && !_jobTimerFrozen)
                return;
            if (_jobTiming)
                _jobElapsed = DateTime.UtcNow - _jobStartedUtc;
            _jobTiming = false;
            _jobTimerFrozen = false;
            ShowJobTime();
            AppendLog("Job time " + JobTimeText() + ".");
        }

        void FreezeJobTimer()
        {
            if (!_jobTiming)
                return;
            _jobElapsed = DateTime.UtcNow - _jobStartedUtc;
            _jobTiming = false;
            _jobTimerFrozen = true;
            ShowJobTime();
        }

        void ThawJobTimer()
        {
            if (!_jobTimerFrozen)
                return;
            _jobStartedUtc = DateTime.UtcNow - _jobElapsed;
            _jobTiming = true;
            _jobTimerFrozen = false;
            ShowJobTime();
        }

        void ShowJobTime()
        {
            if (_jobTime == null)
                return;
            _jobTime.Text = JobTimeText();
        }

        string JobTimeText()
        {
            TimeSpan span = _jobTiming ? DateTime.UtcNow - _jobStartedUtc : _jobElapsed;
            int minutes = (int)span.TotalMinutes;
            if (minutes < 0)
                minutes = 0;
            return minutes.ToString(CultureInfo.InvariantCulture) + ":" + span.Seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        bool CanMoveMachine()
        {
            if (!_link.IsOpen || !_ready || !_havePos)
            {
                AppendLog("Connect and wait for a position first.");
                return false;
            }
            if (_jogOutstanding || _motionQueue.Count > 0 || _scribeActive || _loadActive || (_state.Length > 0 && _state != "Idle"))
            {
                AppendLog("Wait until the machine is idle.");
                return false;
            }
            return true;
        }

        void QueueTravelHome()
        {
            double feed = ReadFeed();
            bool inches = _reportInches;
            if (inches)
                feed = feed / 25.4;
            double plunge = feed * 0.5;
            if (plunge < (inches ? 30 / 25.4 : 30))
                plunge = inches ? 30 / 25.4 : 30;
            if (plunge > feed)
                plunge = feed;
            double lift = ReadLift();
            if (inches)
                lift = lift / 25.4;
            if (lift < (inches ? 2 / 25.4 : 2))
                lift = inches ? 2 / 25.4 : 2;
            _motionQueue.Clear();
            _motionQueue.Add(inches ? "G20" : "G21");
            _motionQueue.Add("G90");
            _motionQueue.Add("G94");
            _motionQueue.Add(string.Format(CultureInfo.InvariantCulture, "G91 G1 Z{0:0.###} F{1:0.##}", lift, plunge));
            _motionQueue.Add("G90");
            _motionQueue.Add(string.Format(CultureInfo.InvariantCulture, "G1 X0 F{0:0.##}", feed));
            _motionQueue.Add(string.Format(CultureInfo.InvariantCulture, "G1 Y0 F{0:0.##}", feed));
            _motionQueue.Add(string.Format(CultureInfo.InvariantCulture, "G1 Z0 F{0:0.##}", plunge));
        }

        void GoToWorkHome()
        {
            if (!CanMoveMachine())
                return;
            if (!_homeSet)
            {
                AppendLog("Set Home on the template mark before going home.");
                return;
            }
            DialogResult answer = MessageBox.Show(this,
                "Raise the bit, move X back to 0, then Y back to 0, then lower to Z0?",
                "Go home",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;
            _returnHomeActive = true;
            QueueTravelHome();
            AppendLog("Returning home on X first, then Y, then Z0.");
            PumpMotion();
        }

        void LoadPlates()
        {
            if (!CanMoveMachine())
                return;
            if (!_homeSet)
            {
                AppendLog("Set Home on the template mark before loading plates.");
                return;
            }
            double targetZ = _reportInches ? 15.0 / 25.4 : 15.0;
            if (_curZ >= targetZ - 0.05)
            {
                AppendLog("Z is already 15 mm or more above home.");
                return;
            }
            DialogResult answer = MessageBox.Show(this,
                "Raise Z to 15 mm above home so plates can be loaded?",
                "Load plates",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;
            double feed = ReadFeed();
            if (_reportInches)
                feed = feed / 25.4;
            _loadActive = true;
            _motionQueue.Clear();
            _motionQueue.Add(_reportInches ? "G20" : "G21");
            _motionQueue.Add("G90");
            _motionQueue.Add("G94");
            _motionQueue.Add(string.Format(CultureInfo.InvariantCulture, "G1 Z{0:0.###} F{1:0.##}", targetZ, feed));
            AppendLog("Raising Z to 15 mm above home.");
            PumpMotion();
        }

        void PollStatus()
        {
            if (_jobTiming)
                ShowJobTime();
            if (!_link.IsOpen)
                return;
            if (_jogOutstanding && (DateTime.UtcNow - _jogSentUtc).TotalSeconds > 3)
            {
                _jogOutstanding = false;
                AppendLog("No acknowledgement from the controller.");
                UpdateActions();
            }
            try
            {
                _link.WriteRealtime((byte)'?');
            }
            catch (Exception ex)
            {
                AppendLog("Status request failed: " + ex.Message);
            }
        }

        void SendJog(string axis, int sign)
        {
            SendJog(axis, sign, true);
        }

        void SendJog(string axis, int sign, bool reportIfBlocked)
        {
            if (!_link.IsOpen || !_ready)
            {
                if (reportIfBlocked)
                    AppendLog("Wait until the controller answers before jogging.");
                return;
            }
            if (_state == "Alarm")
            {
                if (reportIfBlocked)
                    AppendLog("Machine is locked with an alarm. Use Unlock only after it is safe, or Home if this machine is set up to home.");
                return;
            }
            bool pausedIdle = _jobPaused && (_state == "Idle" || _state.Length == 0);
            if (!pausedIdle && (_motionQueue.Count > 0 || _plateMoveActive))
            {
                if (reportIfBlocked)
                    AppendLog(_jobPaused
                        ? "Paused. Resume continues the job. Jog waits until the controller is idle."
                        : "Wait for the plate move to finish.");
                return;
            }
            if (_state == "Hold")
            {
                if (reportIfBlocked)
                    AppendLog("Paused. Resume continues the job. Jog waits until the controller is idle.");
                return;
            }
            if (_state.Length > 0 && _state != "Idle")
            {
                if (reportIfBlocked)
                    AppendLog("Busy (" + _state + "). Wait for Idle, or press Stop.");
                return;
            }
            if (_jogOutstanding)
                return;

            double step = SelectedStep();
            if (axis == "Z" && step > MaxZStep)
                step = MaxZStep;
            double feed = ReadFeed();
            double distance = step * sign;
            string number = distance.ToString("0.###", CultureInfo.InvariantCulture);
            string command = "$J=G91 G21 " + axis + number + " F" + feed.ToString("0.##", CultureInfo.InvariantCulture);

            _jogOutstanding = true;
            _jogSentUtc = DateTime.UtcNow;
            try
            {
                _link.WriteLine(command);
            }
            catch (Exception ex)
            {
                _jogOutstanding = false;
                AppendLog("Jog failed: " + ex.Message);
                return;
            }

            string shown = distance.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
            string note = "";
            if (axis == "Z" && SelectedStep() > MaxZStep)
                note = " (Z capped at 2 mm)";
            AppendLog("Jog " + axis + " " + shown + " mm at " + feed.ToString("0.##", CultureInfo.InvariantCulture) + " mm/min" + note);
            UpdateActions();
        }

        double SelectedStep()
        {
            if (_step5.Checked)
                return 5;
            if (_step1.Checked)
                return 1;
            return 0.1;
        }

        double ReadFeed()
        {
            double feed;
            string text = _feed.Text.Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out feed)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out feed))
                feed = 300;
            if (feed < 10)
                feed = 10;
            if (feed > MaxFeed)
                feed = MaxFeed;
            return feed;
        }

        double ReadLift()
        {
            double lift;
            string text = _lift.Text.Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out lift)
                && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out lift))
                lift = 5;
            if (lift < 0)
                lift = 0;
            if (lift > 20)
                lift = 20;
            return lift;
        }

        double ReadDepth()
        {
            if (_depth == null)
                return 0.01;
            return (double)SnapDepthValue(_depth.Value);
        }

        static string FormatDepth(double mm)
        {
            return mm.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        void OnDepthChanged()
        {
            SnapDepth();
            RefreshPreview();
        }

        void SnapDepth()
        {
            if (_depth == null || _snappingDepth)
                return;
            decimal snapped = SnapDepthValue(_depth.Value);
            if (snapped == _depth.Value)
                return;
            _snappingDepth = true;
            _depth.Value = snapped;
            _snappingDepth = false;
        }

        static decimal SnapDepthValue(decimal mm)
        {
            if (mm < 0.0025m)
                mm = 0.0025m;
            if (mm > 0.01m)
                mm = 0.01m;
            decimal steps = Math.Round(mm / 0.0025m, 0, MidpointRounding.AwayFromZero);
            if (steps < 1)
                steps = 1;
            if (steps > 4)
                steps = 4;
            return steps * 0.0025m;
        }

        void RefreshPreview()
        {
            if (_preview != null)
                _preview.Invalidate();
        }

        void PlotViewedTop(bool left)
        {
            if (_viewRow > 0)
            {
                if (!left)
                {
                    AppendLog("The right plate copies from row 1. Plot the left top-left for this row.");
                    return;
                }
                PlotUpperSet(_viewRow);
                return;
            }
            PlotPointAt(left ? "Left plate top-left" : "Right plate top-left", left ? _p1Top : _p2Top);
        }

        void PlotViewedBase(string which)
        {
            if (_viewRow > 0)
            {
                AppendLog("Row " + (_viewRow + 1).ToString(CultureInfo.InvariantCulture) + " copies that point from row 1.");
                return;
            }
            if (which == "bottom-left")
                PlotPointAt("Left plate bottom-left", _p1Bottom);
            else
                PlotPointAt("Left plate right edge", _p1Right);
        }

        void PlotViewedSurface(bool left)
        {
            if (!_havePos || _posKind.Length == 0)
            {
                AppendLog("Wait for a position before plotting Z.");
                return;
            }
            if (!_homeSet)
            {
                AppendLog("Set Home on the template mark before plotting Z.");
                return;
            }
            int index = _viewRow * 2 + (left ? 0 : 1);
            RememberZ(index, _curZ);
            _fixtureKind = _posKind;
            _fixtureInches = _reportInches;
            SaveTemplate();
            RefreshPlateLabels();
            UpdateActions();
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                "Saved {0} Z surface   Z {1:0.000}",
                PlateLabel(index + 1), _curZ));
        }

        void RememberZ(int index, double z)
        {
            if (index < 0 || index >= _zSet.Length)
                return;
            _zSet[index] = true;
            _zValue[index] = z;
            if (index == 0)
            {
                _p1ZSet = true;
                _p1Z = z;
            }
            else if (index == 1)
            {
                _p2ZSet = true;
                _p2Z = z;
            }
        }

        void ForgetZ(int index)
        {
            if (index < 0 || index >= _zSet.Length)
                return;
            _zSet[index] = false;
            _zValue[index] = 0;
            if (index == 0)
                _p1ZSet = false;
            else if (index == 1)
                _p2ZSet = false;
        }

        bool HasZ(int index)
        {
            return index >= 0 && index < _zSet.Length && _zSet[index];
        }

        void DisplayedSide(bool left, out PlotPoint top, out PlotPoint bottom, out PlotPoint right, out bool zSet, out double z)
        {
            top = new PlotPoint();
            bottom = new PlotPoint();
            right = new PlotPoint();
            zSet = false;
            z = 0;
            int zIndex = _viewRow * 2 + (left ? 0 : 1);
            if (HasZ(zIndex))
            {
                zSet = true;
                z = _zValue[zIndex];
            }
            if (_viewRow <= 0)
            {
                if (left)
                {
                    top = CopyPoint(_p1Top);
                    bottom = CopyPoint(_p1Bottom);
                    right = CopyPoint(_p1Right);
                }
                else
                {
                    top = CopyPoint(_p2Top);
                    bottom = CopyPoint(_p2Bottom);
                    right = CopyPoint(_p2Right);
                }
                return;
            }
            if (_viewRow - 1 >= _upperSet.Length || !_upperSet[_viewRow - 1].Set)
                return;
            PlotPoint origin = _upperSet[_viewRow - 1];
            double ox = origin.X;
            double oy = origin.Y;
            if (!left)
            {
                if (!_p1Top.Set || !_p2Top.Set)
                    return;
                ox += _p2Top.X - _p1Top.X;
                oy += _p2Top.Y - _p1Top.Y;
            }
            top.Set = true;
            top.X = ox;
            top.Y = oy;
            top.Z = origin.Z;
            if (_p1Top.Set && _p1Bottom.Set)
            {
                bottom.Set = true;
                bottom.X = ox + (_p1Bottom.X - _p1Top.X);
                bottom.Y = oy + (_p1Bottom.Y - _p1Top.Y);
            }
            if (_p1Top.Set && _p1Right.Set)
            {
                right.Set = true;
                right.X = ox + (_p1Right.X - _p1Top.X);
                right.Y = oy + (_p1Right.Y - _p1Top.Y);
            }
        }

        static PlotPoint CopyPoint(PlotPoint source)
        {
            var point = new PlotPoint();
            if (source != null && source.Set)
            {
                point.Set = true;
                point.X = source.X;
                point.Y = source.Y;
                point.Z = source.Z;
            }
            return point;
        }

        void PlotPointAt(string name, PlotPoint point)
        {
            if (!_havePos || _posKind.Length == 0)
            {
                AppendLog("Wait for a position before plotting.");
                return;
            }
            if (_fixtureKind.Length > 0 && _fixtureKind != _posKind)
            {
                AppendLog("This position is " + _posKind + " and the saved points are " + _fixtureKind + ". Clear the plates and plot them again in one session.");
                return;
            }
            _fixtureKind = _posKind;
            _fixtureInches = _reportInches;
            point.Set = true;
            point.X = _curX;
            point.Y = _curY;
            point.Z = _curZ;
            if (_p1Top.Set && _p1Bottom.Set && Math.Abs(_p1Top.X - _p1Bottom.X) > 0.2)
                AppendLog("Left plate bottom is not directly under the top-left. The left edge stays on the top-left X.");
            bool copied = CopyRightPlateFromLeft();
            SaveTemplate();
            RefreshPlateLabels();
            UpdateActions();
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                "Saved {0}   X {1:0.000}   Y {2:0.000}   Z {3:0.000}", name, point.X, point.Y, point.Z));
            if (copied)
                AppendLog("Right plate height and width now match the left plate. Plot the left and right Z separately.");
            else if (point == _p2Top)
                AppendLog("Right top-left saved. Finish the left plate and this size will copy from it.");
        }

        bool CopyRightPlateFromLeft()
        {
            if (!_p2Top.Set || !_p1Top.Set || !_p1Bottom.Set || !_p1Right.Set)
                return false;
            _p2Bottom.X = _p2Top.X + (_p1Bottom.X - _p1Top.X);
            _p2Bottom.Y = _p2Top.Y + (_p1Bottom.Y - _p1Top.Y);
            _p2Bottom.Z = _p2Top.Z;
            _p2Bottom.Set = true;
            _p2Right.X = _p2Top.X + (_p1Right.X - _p1Top.X);
            _p2Right.Y = _p2Top.Y + (_p1Right.Y - _p1Top.Y);
            _p2Right.Z = _p2Top.Z;
            _p2Right.Set = true;
            return true;
        }

        void ClearPlate(int plate)
        {
            if (_viewRow > 0)
            {
                int index = _viewRow * 2 + (plate == 1 ? 0 : 1);
                if (plate == 1)
                    _upperSet[_viewRow - 1].Set = false;
                ForgetZ(index);
                AppendLog("Cleared " + PlateLabel(index + 1) + (plate == 1 ? " top-left and Z." : " Z."));
                SaveTemplate();
                RefreshPlateLabels();
                UpdateActions();
                return;
            }
            if (plate == 1)
            {
                _p1Top.Set = false;
                _p1Bottom.Set = false;
                _p1Right.Set = false;
                ForgetZ(0);
                _p2Bottom.Set = false;
                _p2Right.Set = false;
                AppendLog(_p2Top.Set
                    ? "Cleared the left plate. The right plate size was cleared too. Its top-left and Z are still saved."
                    : "Cleared the left plate.");
            }
            else
            {
                _p2Top.Set = false;
                _p2Bottom.Set = false;
                _p2Right.Set = false;
                ForgetZ(1);
                AppendLog("Cleared the right plate.");
            }
            if (!_p1Top.Set && !_p1Bottom.Set && !_p1Right.Set && !_p1ZSet
                && !_p2Top.Set && !_p2Bottom.Set && !_p2Right.Set && !_p2ZSet)
                _fixtureKind = "";
            SaveTemplate();
            RefreshPlateLabels();
            UpdateActions();
        }

        void GoToPlate(int plate)
        {
            bool left = plate == 1;
            PlotPoint top;
            PlotPoint bottom;
            PlotPoint right;
            bool zSet;
            double z;
            DisplayedSide(left, out top, out bottom, out right, out zSet, out z);
            if (!top.Set)
            {
                AppendLog("Plot the " + (left ? "left" : "right") + " plate top-left first.");
                return;
            }
            if (!_link.IsOpen || !_ready || !_havePos)
            {
                AppendLog("Connect and wait for a position before moving.");
                return;
            }
            if (_jogOutstanding || _motionQueue.Count > 0 || (_state.Length > 0 && _state != "Idle"))
            {
                AppendLog("Wait until the machine is idle.");
                return;
            }
            string coordError;
            if (!SameCoordinateSystem(out coordError))
            {
                AppendLog(coordError);
                return;
            }

            bool inches = _fixtureInches;
            double feed = ReadFeed();
            if (inches)
                feed = feed / 25.4;
            string unit = inches ? "G20" : "G21";
            double liftMm = ReadLift();
            double lift = inches ? liftMm / 25.4 : liftMm;
            double surface = top.Z;
            if (zSet)
                surface = z;
            double raise = (surface + lift) - _curZ;
            double raiseMm = inches ? raise * 25.4 : raise;
            if (raiseMm > 30)
            {
                AppendLog("Saved Z and the current Z are far apart. Using the lift amount only.");
                raise = lift;
            }

            _plateMoveActive = true;
            if (raise > 0.02)
            {
                string zMove = string.Format(CultureInfo.InvariantCulture,
                    "$J=G91 {0} Z{1:0.###} F{2:0.##}", unit, raise, feed);
                _motionQueue.Add(zMove);
                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "Raising Z {0:0.000} mm so the bit clears the plates.", inches ? raise * 25.4 : raise));
            }
            else
            {
                AppendLog("Z is already clear. Moving across at the current height.");
            }

            string xyMove;
            if (_fixtureKind == "MPos")
            {
                xyMove = string.Format(CultureInfo.InvariantCulture,
                    "$J=G53 {0} X{1:0.###} Y{2:0.###} F{3:0.##}", unit, top.X, top.Y, feed);
            }
            else
            {
                xyMove = string.Format(CultureInfo.InvariantCulture,
                    "$J=G90 {0} X{1:0.###} Y{2:0.###} F{3:0.##}", unit, top.X, top.Y, feed);
            }
            _motionQueue.Add(xyMove);
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                "Moving to row {0} {1} top-left   X {2:0.000}   Y {3:0.000}",
                _viewRow + 1, left ? "left" : "right", top.X, top.Y));
            PumpMotion();
        }

        void StartScribe()
        {
            if (!_link.IsOpen || !_ready || !_havePos)
            {
                AppendLog("Connect and wait for the controller before scribing.");
                return;
            }
            if (!_homeSet)
            {
                AppendLog("Set Home on the template mark before scribing.");
                return;
            }
            if (_jogOutstanding || _motionQueue.Count > 0 || _scribeActive || (_state.Length > 0 && _state != "Idle"))
            {
                AppendLog("Wait until the machine is idle.");
                return;
            }
            List<int> plates = SelectedPlates();
            if (plates.Count == 0)
            {
                AppendLog("Select at least one row.");
                return;
            }
            string plateError;
            for (int i = 0; i < plates.Count; i++)
            {
                if (!TryPlateBox(plates[i], out plateError))
                {
                    AppendLog(plateError);
                    return;
                }
            }
            string coordError;
            if (!SameCoordinateSystem(out coordError))
            {
                AppendLog(coordError);
                return;
            }

            FlushViewedProduct();
            string what;
            string textError;
            if (!TryJobText(plates, out what, out textError))
            {
                AppendLog(textError);
                return;
            }

            double depth = ReadDepth();
            string where = SelectedPlateText();
            string plan = ScribePlan(plates, depth);
            DialogResult ready = MessageBox.Show(this,
                "Are you ready to scribe with the current settings? And are the selected plates loaded and film has been removed?",
                "Start scribing",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (ready != DialogResult.OK)
                return;

            DialogResult start = MessageBox.Show(this,
                "Is the Green Spindle button engaged and the CNC is clear from debris and hazards? Clicking OK Starts the CNC process",
                "Start scribing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            if (start != DialogResult.OK)
                return;

            List<string> lines = BuildScribe(depth, plates);
            _scribeActive = true;
            _motionQueue.Clear();
            for (int i = 0; i < lines.Count; i++)
                _motionQueue.Add(lines[i]);
            StartJobTimer();
            AppendLog("Scribing " + what + " at " + FormatDepth(depth) + " mm on " + where + ". " + plan);
            PumpMotion();
        }

        bool SameCoordinateSystem(out string error)
        {
            error = "";
            if (_fixtureKind != "MPos" && _fixtureKind != "WPos")
            {
                error = "Plot the plates again before moving. Their coordinate system was not saved.";
                return false;
            }
            if (_posKind != _fixtureKind)
            {
                error = "The plates were plotted as " + CoordName(_fixtureKind)
                    + ", but the controller is reporting " + CoordName(_posKind)
                    + ". Clear the plates and plot them again.";
                return false;
            }
            if (_fixtureInches != _reportInches)
            {
                error = "The plates were plotted in " + (_fixtureInches ? "inches" : "mm")
                    + ", but the controller is reporting " + (_reportInches ? "inches" : "mm")
                    + ". Clear the plates and plot them again.";
                return false;
            }
            return true;
        }

        static string CoordName(string kind)
        {
            return kind == "MPos" ? "machine position" : "work position";
        }

        string ScribePlan(List<int> plates, double depthMm)
        {
            double depth = ToFixtureUnits(depthMm);
            double topX;
            double topY;
            double botX;
            double botY;
            double rightX;
            double rightY;
            double surface;
            string error = "";
            if (plates.Count == 0 || !TryPlateBox(plates[0], out topX, out topY, out botX, out botY, out rightX, out rightY, out surface, out error))
                return error;
            string first = string.Format(CultureInfo.InvariantCulture,
                "Plate {4} corner  X {0:0.000}   Y {1:0.000}   cut Z {2:0.000}   ({3})",
                topX, topY, surface - depth, CoordName(_fixtureKind), PlateLabel(plates[0]));
            if (plates.Count == 1)
                return first;
            var rest = new StringBuilder();
            for (int i = 1; i < plates.Count; i++)
            {
                if (rest.Length > 0)
                    rest.Append(", ");
                rest.Append(PlateLabel(plates[i]));
            }
            return first + "\r\nThen plates " + rest.ToString() + ".";
        }

        bool TryPlateBox(int plateNumber, out string error)
        {
            double topX;
            double topY;
            double botX;
            double botY;
            double rightX;
            double rightY;
            double surface;
            return TryPlateBox(plateNumber, out topX, out topY, out botX, out botY, out rightX, out rightY, out surface, out error);
        }

        bool TryPlateBox(int plateNumber, out double topX, out double topY, out double botX, out double botY, out double rightX, out double rightY, out double surface, out string error)
        {
            topX = 0;
            topY = 0;
            botX = 0;
            botY = 0;
            rightX = 0;
            rightY = 0;
            surface = 0;
            error = "";
            if (!_p1Top.Set || !_p1Bottom.Set || !_p1Right.Set)
            {
                error = "Plot row 1 left: top-left, bottom-left, and the right edge.";
                return false;
            }
            int row = (plateNumber - 1) / 2;
            int col = (plateNumber - 1) % 2;
            if (col == 1 && !_p2Top.Set)
            {
                error = "Plot the row 1 right top-left. Its Z is plotted on its own.";
                return false;
            }
            double ox;
            double oy;
            if (row == 0)
            {
                ox = col == 0 ? _p1Top.X : _p2Top.X;
                oy = col == 0 ? _p1Top.Y : _p2Top.Y;
            }
            else
            {
                PlotPoint origin = _upperSet[row - 1];
                if (!origin.Set)
                {
                    error = "Plot the left plate top-left on row " + (row + 1).ToString(CultureInfo.InvariantCulture)
                        + ". Size and the right plate copy from row 1. Plot Z on each plate.";
                    return false;
                }
                ox = origin.X;
                oy = origin.Y;
                if (col == 1)
                {
                    ox += _p2Top.X - _p1Top.X;
                    oy += _p2Top.Y - _p1Top.Y;
                }
            }
            topX = ox;
            topY = oy;
            botX = ox + (_p1Bottom.X - _p1Top.X);
            botY = oy + (_p1Bottom.Y - _p1Top.Y);
            rightX = ox + (_p1Right.X - _p1Top.X);
            rightY = oy + (_p1Right.Y - _p1Top.Y);
            if (!HasZ(plateNumber - 1))
            {
                error = PlateLabel(plateNumber) + " needs its own Z surface.";
                return false;
            }
            surface = _zValue[plateNumber - 1];
            if (Math.Abs(topY - botY) < 1)
            {
                error = "Plate " + plateNumber.ToString(CultureInfo.InvariantCulture) + " text height is under 1 mm.";
                return false;
            }
            if (Math.Abs(rightX - topX) < 2)
            {
                error = "Plate " + plateNumber.ToString(CultureInfo.InvariantCulture) + " text width is under 2 mm.";
                return false;
            }
            return true;
        }

        double ToFixtureUnits(double mm)
        {
            if (_fixtureInches)
                return mm / 25.4;
            return mm;
        }

        string MotionZ(double z, double feed)
        {
            if (_fixtureKind == "MPos")
                return string.Format(CultureInfo.InvariantCulture, "G53 G90 G1 Z{0:0.###} F{1:0.##}", z, feed);
            return string.Format(CultureInfo.InvariantCulture, "G1 Z{0:0.###} F{1:0.##}", z, feed);
        }

        string MotionXY(double x, double y, double feed)
        {
            if (_fixtureKind == "MPos")
                return string.Format(CultureInfo.InvariantCulture, "G53 G90 G1 X{0:0.###} Y{1:0.###} F{2:0.##}", x, y, feed);
            return string.Format(CultureInfo.InvariantCulture, "G1 X{0:0.###} Y{1:0.###} F{2:0.##}", x, y, feed);
        }

        string RapidXY(double x, double y)
        {
            if (_fixtureKind == "MPos")
                return string.Format(CultureInfo.InvariantCulture, "G53 G90 G0 X{0:0.###} Y{1:0.###}", x, y);
            return string.Format(CultureInfo.InvariantCulture, "G0 X{0:0.###} Y{1:0.###}", x, y);
        }

        string RapidX(double x)
        {
            if (_fixtureKind == "MPos")
                return string.Format(CultureInfo.InvariantCulture, "G53 G90 G0 X{0:0.###}", x);
            return string.Format(CultureInfo.InvariantCulture, "G0 X{0:0.###}", x);
        }

        string RapidY(double y)
        {
            if (_fixtureKind == "MPos")
                return string.Format(CultureInfo.InvariantCulture, "G53 G90 G0 Y{0:0.###}", y);
            return string.Format(CultureInfo.InvariantCulture, "G0 Y{0:0.###}", y);
        }

        string RapidZ(double z)
        {
            if (_fixtureKind == "MPos")
                return string.Format(CultureInfo.InvariantCulture, "G53 G90 G0 Z{0:0.###}", z);
            return string.Format(CultureInfo.InvariantCulture, "G0 Z{0:0.###}", z);
        }

        bool PlateReady(int plate, out string error)
        {
            string name = plate == 1 ? "Left plate" : "Right plate";
            PlotPoint top = plate == 1 ? _p1Top : _p2Top;
            PlotPoint bottom = plate == 1 ? _p1Bottom : _p2Bottom;
            PlotPoint right = plate == 1 ? _p1Right : _p2Right;
            bool zSet = plate == 1 ? _p1ZSet : _p2ZSet;
            error = "";
            if (!top.Set || !bottom.Set || !right.Set || !zSet)
            {
                if (plate == 2)
                    error = "Plot the left plate fully, then the right plate top-left. Height, width, and Z copy from the left.";
                else
                    error = name + " needs a top-left, bottom-left, right edge, and Z surface.";
                return false;
            }
            if (Math.Abs(top.Y - bottom.Y) < 1)
            {
                error = name + " text height is under 1 mm. Plot the top-left and bottom-left farther apart.";
                return false;
            }
            if (Math.Abs(right.X - top.X) < 2)
            {
                error = name + " text width is under 2 mm. Plot the right edge farther across.";
                return false;
            }
            return true;
        }

        bool TryNormalizeProduct(string raw, int row, out string digits, out string error)
        {
            digits = "";
            error = "";
            string rowName = "Row " + (row + 1).ToString(CultureInfo.InvariantCulture);
            if (raw == null)
                raw = "";
            raw = raw.Trim();
            var kept = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char keptChar;
                if (!TryKeepProductChar(raw[i], out keptChar))
                {
                    error = rowName + " product numbers use letters, digits, and a hyphen.";
                    return false;
                }
                kept.Append(keptChar);
            }
            if (kept.Length == 0)
            {
                error = rowName + " needs a product number.";
                return false;
            }
            digits = kept.ToString();
            return true;
        }

        bool TryJobText(List<int> plates, out string summary, out string error)
        {
            summary = "";
            error = "";
            var text = new StringBuilder();
            var seen = new bool[4];
            for (int i = 0; i < plates.Count; i++)
            {
                int row = (plates[i] - 1) / 2;
                if (row < 0 || row > 3 || seen[row])
                    continue;
                seen[row] = true;
                string digits;
                if (!TryNormalizeProduct(_rowProduct[row], row, out digits, out error))
                    return false;
                int redoStart;
                string redoError;
                if (!TryRedoStart(digits.Length, out redoStart, out redoError))
                {
                    error = "Row " + (row + 1).ToString(CultureInfo.InvariantCulture) + ": " + redoError;
                    return false;
                }
                if (text.Length > 0)
                    text.Append(", ");
                text.Append("row ");
                text.Append((row + 1).ToString(CultureInfo.InvariantCulture));
                text.Append(" ");
                if (redoStart > 0)
                    text.Append("last " + (digits.Length - redoStart).ToString(CultureInfo.InvariantCulture) + " of " + digits);
                else
                    text.Append(digits);
            }
            summary = text.ToString();
            return true;
        }

        void OnRedoChanged()
        {
            if (_redoCount != null && _redo != null)
                _redoCount.Enabled = _redo.Checked;
            RefreshPreview();
        }

        bool TryRedoStart(int length, out int start, out string error)
        {
            start = 0;
            error = "";
            if (_redo == null || !_redo.Checked)
                return true;
            int last = _redoCount == null ? 1 : (int)_redoCount.Value;
            if (last < 1)
            {
                error = "Enter how many last characters to re-do.";
                return false;
            }
            if (last > length)
            {
                error = "Re-do is longer than the product number.";
                return false;
            }
            start = length - last;
            return true;
        }

        int RedoStart(int length)
        {
            int start;
            string error;
            if (!TryRedoStart(length, out start, out error))
                return 0;
            return start;
        }

        static bool TryKeepProductChar(char c, out char kept)
        {
            kept = '\0';
            if (c >= 'a' && c <= 'z')
                c = (char)(c - 32);
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || c == '-' || c == ' ')
            {
                kept = c;
                return true;
            }
            return false;
        }

        List<string> BuildScribe(double depthMm, List<int> plates)
        {
            var lines = new List<string>();
            double feed = ReadFeed();
            double depth = ToFixtureUnits(depthMm);
            double lift = ToFixtureUnits(ReadLift());
            if (_fixtureInches)
                feed = feed / 25.4;
            double plunge = feed * 0.5;
            if (plunge < ToFixtureUnits(30))
                plunge = ToFixtureUnits(30);
            if (plunge > feed)
                plunge = feed;
            if (lift < ToFixtureUnits(2))
                lift = ToFixtureUnits(2);
            lines.Add(_fixtureInches ? "G20" : "G21");
            lines.Add("G90");
            lines.Add("G94");

            double travel = _curZ;
            for (int i = 0; i < plates.Count; i++)
            {
                double topX;
                double topY;
                double botX;
                double botY;
                double rightX;
                double rightY;
                double surface;
                string ignored;
                if (!TryPlateBox(plates[i], out topX, out topY, out botX, out botY, out rightX, out rightY, out surface, out ignored))
                    continue;
                double safe = surface + lift;
                if (safe > travel)
                    travel = safe;
            }
            double[] zNow = new double[] { _curZ };
            if (zNow[0] < travel - 0.02)
            {
                lines.Add(RapidZ(travel));
                zNow[0] = travel;
            }
            double previousTopY = _curY;
            for (int i = 0; i < plates.Count; i++)
            {
                double topX;
                double topY;
                double botX;
                double botY;
                double rightX;
                double rightY;
                double surface;
                string ignored;
                bool climb = false;
                if (TryPlateBox(plates[i], out topX, out topY, out botX, out botY, out rightX, out rightY, out surface, out ignored))
                {
                    climb = topY > previousTopY + 2.0;
                    previousTopY = topY;
                }
                string digits;
                int row = (plates[i] - 1) / 2;
                if (row < 0 || row > 3 || !TryNormalizeProduct(_rowProduct[row], row, out digits, out ignored))
                    continue;
                AppendPlateScribe(lines, plates[i], digits, depth, feed, plunge, lift, zNow, climb, RedoStart(digits.Length));
            }
            double parkLift = ToFixtureUnits(ReadLift());
            if (parkLift < ToFixtureUnits(2))
                parkLift = ToFixtureUnits(2);
            lines.Add(string.Format(CultureInfo.InvariantCulture, "G91 G0 Z{0:0.###}", parkLift));
            lines.Add("G90");
            lines.Add("G0 X0");
            lines.Add("G0 Y0");
            lines.Add(string.Format(CultureInfo.InvariantCulture, "G1 Z0 F{0:0.##}", plunge));
            return lines;
        }

        static void FitGlyph(int count, double height, double width, out double charH, out double charW, out double gap)
        {
            charH = height * 0.90;
            charW = charH * 0.62;
            gap = charH * 0.22;
            if (count <= 0)
                return;
            double total = count * charW + (count - 1) * gap;
            double limit = width * 0.85;
            if (total > limit && total > 0.001)
            {
                double scale = limit / total;
                charH *= scale;
                charW *= scale;
                gap *= scale;
            }
        }

        void AppendPlateScribe(List<string> lines, int plateNumber, string digits, double depth, double feed, double plunge, double lift, double[] zNow, bool climb, int startChar)
        {
            double topX;
            double topY;
            double botX;
            double botY;
            double rightX;
            double rightY;
            double surface;
            string error;
            if (!TryPlateBox(plateNumber, out topX, out topY, out botX, out botY, out rightX, out rightY, out surface, out error))
                return;
            double zCut = surface - depth;
            double zSafe = surface + lift;
            double height = Math.Abs(topY - botY);
            double width = Math.Abs(rightX - topX);
            int ySign = topY >= botY ? 1 : -1;
            int xSign = rightX >= topX ? 1 : -1;
            int count = digits.Length;
            double charH;
            double charW;
            double gap;
            FitGlyph(count, height, width, out charH, out charW, out gap);
            if (zNow[0] < zSafe - 0.02)
            {
                lines.Add(RapidZ(zSafe));
                zNow[0] = zSafe;
            }
            bool climbed = false;
            for (int i = 0; i < count; i++)
            {
                if (i < startChar)
                    continue;
                double along = i * (charW + gap);
                string segs = Glyph(digits[i]);
                for (int s = 0; s < segs.Length; s++)
                {
                    double u1, v1, u2, v2;
                    SegmentEnds(segs[s], out u1, out v1, out u2, out v2);
                    double x1 = topX + xSign * (along + u1 * charW);
                    double y1 = botY + ySign * (v1 * charH);
                    double x2 = topX + xSign * (along + u2 * charW);
                    double y2 = botY + ySign * (v2 * charH);
                    if (climb && !climbed)
                    {
                        lines.Add("G90 G0 X0");
                        lines.Add(RapidY(y1));
                        lines.Add(RapidX(x1));
                        climbed = true;
                    }
                    else
                        lines.Add(RapidXY(x1, y1));
                    lines.Add(MotionZ(zCut, plunge));
                    zNow[0] = zCut;
                    lines.Add(MotionXY(x2, y2, feed));
                    lines.Add(RapidZ(zSafe));
                    zNow[0] = zSafe;
                }
            }
        }

        static string Glyph(char ch)
        {
            if (ch >= 'a' && ch <= 'z')
                ch = (char)(ch - 32);
            if (ch >= '0' && ch <= '9')
                return DigitSegments(ch);
            if (ch == ' ' || ch == '\0')
                return "";
            if (ch == '-') return "s";
            if (ch == 'A') return "abcefg";
            if (ch == 'B') return "abcdefg";
            if (ch == 'C') return "adef";
            if (ch == 'D') return "abcdef";
            if (ch == 'E') return "adefg";
            if (ch == 'F') return "aefg";
            if (ch == 'G') return "acdef";
            if (ch == 'H') return "bcefg";
            if (ch == 'I') return "adh";
            if (ch == 'J') return "bcde";
            if (ch == 'K') return "tvw";
            if (ch == 'L') return "td";
            if (ch == 'M') return "tuop";
            if (ch == 'N') return "tun";
            if (ch == 'O') return "abcdef";
            if (ch == 'P') return "abefg";
            if (ch == 'Q') return "abcdefr";
            if (ch == 'R') return "abefgw";
            if (ch == 'S') return "afgcd";
            if (ch == 'T') return "ah";
            if (ch == 'U') return "bcdef";
            if (ch == 'V') return "xy";
            if (ch == 'W') return "[()]";
            if (ch == 'X') return "nm";
            if (ch == 'Y') return "jlk";
            if (ch == 'Z') return "adn";
            return "";
        }

        static string DigitSegments(char digit)
        {
            if (digit == '0') return "abcdef";
            if (digit == '1') return "bc";
            if (digit == '2') return "abged";
            if (digit == '3') return "abgcd";
            if (digit == '4') return "fgbc";
            if (digit == '5') return "afgcd";
            if (digit == '6') return "afgecd";
            if (digit == '7') return "abc";
            if (digit == '8') return "abcdefg";
            if (digit == '9') return "abgfcd";
            return "";
        }

        static void SegmentEnds(char segment, out double u1, out double v1, out double u2, out double v2)
        {
            u1 = 0.12;
            v1 = 0.5;
            u2 = 0.88;
            v2 = 0.5;
            if (segment == 'a') { u1 = 0.12; v1 = 1.0; u2 = 0.88; v2 = 1.0; }
            else if (segment == 'b') { u1 = 0.88; v1 = 1.0; u2 = 0.88; v2 = 0.5; }
            else if (segment == 'c') { u1 = 0.88; v1 = 0.5; u2 = 0.88; v2 = 0.0; }
            else if (segment == 'd') { u1 = 0.88; v1 = 0.0; u2 = 0.12; v2 = 0.0; }
            else if (segment == 'e') { u1 = 0.12; v1 = 0.0; u2 = 0.12; v2 = 0.5; }
            else if (segment == 'f') { u1 = 0.12; v1 = 0.5; u2 = 0.12; v2 = 1.0; }
            else if (segment == 'g') { u1 = 0.12; v1 = 0.5; u2 = 0.88; v2 = 0.5; }
            else if (segment == 'h') { u1 = 0.50; v1 = 0.0; u2 = 0.50; v2 = 1.0; }
            else if (segment == 's') { u1 = 0.22; v1 = 0.5; u2 = 0.78; v2 = 0.5; }
            else if (segment == 't') { u1 = 0.12; v1 = 0.0; u2 = 0.12; v2 = 1.0; }
            else if (segment == 'u') { u1 = 0.88; v1 = 0.0; u2 = 0.88; v2 = 1.0; }
            else if (segment == 'n') { u1 = 0.12; v1 = 1.0; u2 = 0.88; v2 = 0.0; }
            else if (segment == 'm') { u1 = 0.88; v1 = 1.0; u2 = 0.12; v2 = 0.0; }
            else if (segment == 'o') { u1 = 0.12; v1 = 1.0; u2 = 0.50; v2 = 0.35; }
            else if (segment == 'p') { u1 = 0.88; v1 = 1.0; u2 = 0.50; v2 = 0.35; }
            else if (segment == 'v') { u1 = 0.12; v1 = 0.5; u2 = 0.88; v2 = 1.0; }
            else if (segment == 'w') { u1 = 0.12; v1 = 0.5; u2 = 0.88; v2 = 0.0; }
            else if (segment == 'x') { u1 = 0.12; v1 = 1.0; u2 = 0.50; v2 = 0.0; }
            else if (segment == 'y') { u1 = 0.88; v1 = 1.0; u2 = 0.50; v2 = 0.0; }
            else if (segment == 'j') { u1 = 0.12; v1 = 1.0; u2 = 0.50; v2 = 0.5; }
            else if (segment == 'l') { u1 = 0.88; v1 = 1.0; u2 = 0.50; v2 = 0.5; }
            else if (segment == 'k') { u1 = 0.50; v1 = 0.5; u2 = 0.50; v2 = 0.0; }
            else if (segment == 'r') { u1 = 0.55; v1 = 0.15; u2 = 0.88; v2 = 0.0; }
            else if (segment == '[') { u1 = 0.12; v1 = 1.0; u2 = 0.34; v2 = 0.0; }
            else if (segment == '(') { u1 = 0.34; v1 = 0.0; u2 = 0.50; v2 = 0.42; }
            else if (segment == ')') { u1 = 0.50; v1 = 0.42; u2 = 0.66; v2 = 0.0; }
            else if (segment == ']') { u1 = 0.66; v1 = 0.0; u2 = 0.88; v2 = 1.0; }
        }

        void PumpMotion()
        {
            if (_jobPaused || _jogOutstanding || _motionQueue.Count == 0 || !_link.IsOpen)
                return;
            string command = _motionQueue[0];
            _motionQueue.RemoveAt(0);
            _jogOutstanding = true;
            _jogSentUtc = DateTime.UtcNow;
            try
            {
                _link.WriteLine(command);
            }
            catch (Exception ex)
            {
                _jogOutstanding = false;
                _motionQueue.Clear();
                _plateMoveActive = false;
                _scribeActive = false;
                AppendLog("Move failed: " + ex.Message);
            }
            UpdateActions();
        }

        void RefreshPlateLabels()
        {
            if (_coord1Top == null)
                return;
            int shown = _viewRow + 1;
            if (_leftTitle != null)
                _leftTitle.Text = shown == 1 ? "Left plate" : "Row " + shown.ToString(CultureInfo.InvariantCulture) + " left";
            if (_rightTitle != null)
                _rightTitle.Text = shown == 1 ? "Right plate" : "Row " + shown.ToString(CultureInfo.InvariantCulture) + " right";

            PlotPoint leftTop;
            PlotPoint leftBottom;
            PlotPoint leftRight;
            bool leftZSet;
            double leftZ;
            DisplayedSide(true, out leftTop, out leftBottom, out leftRight, out leftZSet, out leftZ);
            PlotPoint rightTop;
            PlotPoint rightBottom;
            PlotPoint rightRight;
            bool rightZSet;
            double rightZ;
            DisplayedSide(false, out rightTop, out rightBottom, out rightRight, out rightZSet, out rightZ);

            _coord1Top.Text = FormatXY(leftTop);
            _coord1Bottom.Text = FormatXY(leftBottom);
            _coord1Right.Text = FormatXY(leftRight);
            _coord1Z.Text = FormatZ(leftZSet, leftZ);
            _coord2Top.Text = FormatXY(rightTop);
            _coord2Bottom.Text = FormatXY(rightBottom);
            _coord2Right.Text = FormatXY(rightRight);
            _coord2Z.Text = FormatZ(rightZSet, rightZ);

            if (_viewRow == 0)
            {
                _plateSize.Text = SizeText(_p1Top, _p1Bottom, _p1Right);
                if (_p2Top.Set && _p2Bottom.Set && _p2Right.Set)
                    _plate2Size.Text = SizeText(_p2Top, _p2Bottom, _p2Right);
                else
                    _plate2Size.Text = "Plot the top-left and this plate's Z.";
                _plot1Top.Text = "Plot top-left";
                _plot1Bottom.Text = "Plot bottom-left";
                _plot1Right.Text = "Plot right edge";
                _plot1Z.Text = "Plot Z surface";
                _plot2Top.Text = "Plot top-left";
                _plot2Z.Text = "Plot Z surface";
            }
            else if (!leftTop.Set)
            {
                _plateSize.Text = "Plot the left top-left. Size copies from row 1. Plot this Z.";
                _plate2Size.Text = "Size copies from row 1. Plot this Z.";
                _plot1Top.Text = "Plot top-left";
                _plot1Bottom.Text = "Copied";
                _plot1Right.Text = "Copied";
                _plot1Z.Text = "Plot Z surface";
                _plot2Top.Text = "Copied";
                _plot2Z.Text = "Plot Z surface";
            }
            else
            {
                _plateSize.Text = SizeText(leftTop, leftBottom, leftRight);
                _plate2Size.Text = rightTop.Set ? SizeText(rightTop, rightBottom, rightRight) : "Copies from row 1. Plot this Z.";
                _plot1Top.Text = "Plot top-left";
                _plot1Bottom.Text = "Copied";
                _plot1Right.Text = "Copied";
                _plot1Z.Text = "Plot Z surface";
                _plot2Top.Text = "Copied";
                _plot2Z.Text = "Plot Z surface";
            }
            RefreshPreview();
            RefreshPlateGrid();
        }

        string SizeText(PlotPoint top, PlotPoint bottom, PlotPoint right)
        {
            if (!(top.Set && bottom.Set && right.Set))
                return "Plot top-left, bottom-left, right edge, and Z.";
            double height = Math.Abs(top.Y - bottom.Y);
            double width = Math.Abs(right.X - top.X);
            string unit = _fixtureInches ? "in" : "mm";
            return string.Format(CultureInfo.InvariantCulture, "H {0:0.000} {2}    W {1:0.000} {2}", height, width, unit);
        }

        static string FormatZ(bool set, double z)
        {
            if (!set)
                return "Z not set";
            return string.Format(CultureInfo.InvariantCulture, "Z {0,8:0.000}", z);
        }

        void PlotSurface(int plate)
        {
            if (!_havePos || _posKind.Length == 0)
            {
                AppendLog("Wait for a position before plotting Z.");
                return;
            }
            if (!_homeSet)
            {
                AppendLog("Set Home on the template mark before plotting Z.");
                return;
            }
            string name = plate == 1 ? "Left plate" : "Right plate";
            RememberZ(plate - 1, _curZ);
            _fixtureKind = _posKind;
            _fixtureInches = _reportInches;
            SaveTemplate();
            RefreshPlateLabels();
            UpdateActions();
            AppendLog(string.Format(CultureInfo.InvariantCulture, "Saved {0} surface   Z {1:0.000}", name, _curZ));
        }

        void PlotUpperSet(int row)
        {
            if (row < 1 || row > 3)
                return;
            if (!_havePos || _posKind.Length == 0)
            {
                AppendLog("Wait for a position before plotting.");
                return;
            }
            if (!_homeSet)
            {
                AppendLog("Set Home on the template mark before plotting the next plates.");
                return;
            }
            if (_fixtureKind.Length > 0 && _fixtureKind != _posKind)
            {
                AppendLog("This position is " + _posKind + " and the saved points are " + _fixtureKind + ". Clear the plates and plot them again in one session.");
                return;
            }
            if (!_p1Top.Set || !_p1Bottom.Set || !_p1Right.Set || !_p2Top.Set)
            {
                AppendLog("Plot row 1 first. The left plate needs its outline, and the right plate needs its top-left. Plot Z on each plate.");
                return;
            }
            _fixtureKind = _posKind;
            _fixtureInches = _reportInches;
            PlotPoint point = _upperSet[row - 1];
            point.Set = true;
            point.X = _curX;
            point.Y = _curY;
            point.Z = _curZ;
            ViewRow(row);
            SaveTemplate();
            RefreshPlateLabels();
            UpdateActions();
            AppendLog(string.Format(CultureInfo.InvariantCulture,
                "Row {0} left top-left   X {1:0.000}   Y {2:0.000}. Size and the right plate copy from row 1. Plot Z on each plate.",
                row + 1, point.X, point.Y));
        }

        void ParseZ(string value, int plate)
        {
            int index = plate - 1;
            if (index < 0 || index >= _zSet.Length)
                return;
            if (value.Length == 0)
            {
                ForgetZ(index);
                return;
            }
            double z;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return;
            RememberZ(index, z);
        }

        static string FormatXY(PlotPoint point)
        {
            if (point == null || !point.Set)
                return "not set";
            return string.Format(CultureInfo.InvariantCulture, "X {0,8:0.000}   Y {1,8:0.000}", point.X, point.Y);
        }

        string TemplatePath()
        {
            string folder = Path.GetDirectoryName(Application.ExecutablePath);
            return Path.Combine(folder, "plate-template.txt");
        }

        void LoadProductCatalog()
        {
            if (_product == null)
                return;
            try
            {
                string folder = Path.GetDirectoryName(Application.ExecutablePath);
                if (folder == null || !Directory.Exists(folder))
                    return;
                string[] files = Directory.GetFiles(folder, "*.csv");
                var numbers = new List<string>();
                for (int i = 0; i < files.Length; i++)
                    ReadProductCsv(files[i], numbers);
                var source = new AutoCompleteStringCollection();
                for (int i = 0; i < numbers.Count; i++)
                    source.Add(numbers[i]);
                _product.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                _product.AutoCompleteSource = AutoCompleteSource.CustomSource;
                _product.AutoCompleteCustomSource = source;
                if (numbers.Count > 0)
                    AppendLog("Product list loaded. " + numbers.Count.ToString(CultureInfo.InvariantCulture) + " numbers from the csv file beside the app.");
            }
            catch (Exception ex)
            {
                AppendLog("Could not read the product csv: " + ex.Message);
            }
        }

        void ReadProductCsv(string path, List<string> numbers)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0)
                return;
            int column = 0;
            int start = 0;
            List<string> first = SplitCsvLine(lines[0]);
            if (first.Count > 0 && LooksLikeHeader(first))
            {
                column = ProductColumn(first);
                start = 1;
            }
            for (int i = start; i < lines.Length; i++)
            {
                if (lines[i] == null || lines[i].Trim().Length == 0)
                    continue;
                List<string> cells = SplitCsvLine(lines[i]);
                if (column >= cells.Count)
                    continue;
                string code = NormalizeCatalogCode(cells[column]);
                if (code.Length == 0)
                    continue;
                if (!CatalogHas(numbers, code))
                    numbers.Add(code);
            }
        }

        static int ProductColumn(List<string> header)
        {
            for (int i = 0; i < header.Count; i++)
            {
                string cell = header[i] == null ? "" : header[i].Trim().ToLowerInvariant();
                if (cell.IndexOf("product") >= 0 || cell.IndexOf("part") >= 0 || cell.IndexOf("item") >= 0 || cell == "sku")
                    return i;
            }
            return 0;
        }

        static bool LooksLikeHeader(List<string> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                string cell = cells[i] == null ? "" : cells[i].Trim().ToLowerInvariant();
                if (cell.IndexOf("product") >= 0 || cell.IndexOf("part") >= 0 || cell.IndexOf("item") >= 0 || cell == "sku" || cell.IndexOf("number") >= 0)
                    return true;
            }
            return false;
        }

        static string NormalizeCatalogCode(string raw)
        {
            if (raw == null)
                return "";
            raw = raw.Trim();
            if (raw.Length == 0 || raw.Length > 24)
                return "";
            var kept = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char keptChar;
                if (!TryKeepProductChar(raw[i], out keptChar))
                    return "";
                kept.Append(keptChar);
            }
            return kept.ToString().Trim();
        }

        static bool CatalogHas(List<string> numbers, string code)
        {
            for (int i = 0; i < numbers.Count; i++)
            {
                if (string.Equals(numbers[i], code, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static List<string> SplitCsvLine(string line)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            if (line == null)
                line = "";
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                            quoted = false;
                    }
                    else
                        cell.Append(c);
                }
                else if (c == '"')
                    quoted = true;
                else if (c == ',')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Length = 0;
                }
                else if (c != '\r')
                    cell.Append(c);
            }
            cells.Add(cell.ToString().Trim());
            return cells;
        }

        void LoadTemplate()
        {
            try
            {
                string path = TemplatePath();
                if (!File.Exists(path))
                {
                    RefreshPlateLabels();
                    RefreshPlateGrid();
                    return;
                }
                ReadSettings(File.ReadAllLines(path));
                if (CopyRightPlateFromLeft())
                    SaveTemplate();
                RefreshPlateLabels();
                RefreshPlateGrid();
                SyncPlateCountBox();
                RefreshTrueHomeLabel();
                if (_p1Top.Set || _p2Top.Set)
                    AppendLog("Loaded saved plate positions. Set Home on the template mark before scribing.");
            }
            catch (Exception ex)
            {
                AppendLog("Could not read saved plate positions: " + ex.Message);
            }
        }

        void ReadSettings(string[] lines)
        {
            _platesFromFile = false;
            _legacyProductSeen = false;
            _legacyProduct = "";
            for (int i = 0; i < _rowProductSeen.Length; i++)
            {
                _rowProductSeen[i] = false;
                _rowProduct[i] = "";
            }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int split = line.IndexOf('=');
                if (split <= 0)
                    continue;
                ApplySetting(line.Substring(0, split), line.Substring(split + 1).Trim());
            }
            FinishRowProducts();
        }

        void FinishRowProducts()
        {
            for (int i = 0; i < _rowProduct.Length; i++)
            {
                if (!_rowProductSeen[i] && _legacyProductSeen)
                    _rowProduct[i] = _legacyProduct;
            }
            ShowViewedProduct();
        }

        void ApplySetting(string key, string value)
        {
            if (key == "kind")
                _fixtureKind = value;
            else if (key == "inches")
                _fixtureInches = value == "1";
            else if (key == "p1top")
                ParsePoint(value, _p1Top);
            else if (key == "p1bottom")
                ParsePoint(value, _p1Bottom);
            else if (key == "p1right")
                ParsePoint(value, _p1Right);
            else if (key == "p2top")
                ParsePoint(value, _p2Top);
            else if (key == "p2bottom")
                ParsePoint(value, _p2Bottom);
            else if (key == "p2right")
                ParsePoint(value, _p2Right);
            else if (key == "p1z")
                ParseZ(value, 1);
            else if (key == "p2z")
                ParseZ(value, 2);
            else if (key.Length >= 2 && key[0] == 'z')
            {
                int plateNumber;
                if (int.TryParse(key.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out plateNumber)
                    && plateNumber >= 1 && plateNumber <= 8)
                    ParseZ(value, plateNumber);
            }
            else if (key.Length == 4 && key.StartsWith("set"))
            {
                int setNumber;
                if (int.TryParse(key.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out setNumber)
                    && setNumber >= 2 && setNumber <= 4)
                    ParsePoint(value, _upperSet[setNumber - 2]);
            }
            else if (key == "trueHome")
                _trueHomeSet = value == "1";
            else if (key == "lastInches")
                _lastInches = value == "1";
            else if (key == "last")
                ParseLast(value);
            else if (key == "depth")
                ApplyStoredDepth(value);
            else if (key == "lift" && _lift != null)
                _lift.Text = value;
            else if (key.Length == 8 && key.StartsWith("product"))
            {
                int rowNumber;
                if (int.TryParse(key.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out rowNumber)
                    && rowNumber >= 1 && rowNumber <= 4)
                {
                    _rowProduct[rowNumber - 1] = value;
                    _rowProductSeen[rowNumber - 1] = true;
                }
            }
            else if (key == "product")
            {
                _legacyProduct = value;
                _legacyProductSeen = true;
            }
            else if (key == "applyAll" && _applyAll != null)
            {
                _applyAllQuiet = true;
                _applyAll.Checked = value == "1";
                _applyAllQuiet = false;
            }
            else if (key == "redo" && _redo != null)
                _redo.Checked = value == "1";
            else if (key == "redoLast" && _redoCount != null)
            {
                int last;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out last))
                {
                    if (last < (int)_redoCount.Minimum)
                        last = (int)_redoCount.Minimum;
                    if (last > (int)_redoCount.Maximum)
                        last = (int)_redoCount.Maximum;
                    _redoCount.Value = last;
                }
            }
            else if (key == "feed" && _feed != null)
                _feed.Text = value;
            else if (key == "display")
                _savedDisplay = value;
            else if (key == "rows")
                ParseRows(value);
            else if (key == "plates")
                ParsePlateSelection(value);
        }

        void ApplyStoredDepth(string value)
        {
            if (_depth == null || value.Length == 0)
                return;
            decimal mm;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out mm))
                return;
            _depth.Value = SnapDepthValue(mm);
        }

        void ParseRows(string value)
        {
            if (_platesFromFile)
                return;
            for (int i = 0; i < _plateOn.Length; i++)
                _plateOn[i] = false;
            string[] parts = value.Split(',');
            bool any = false;
            for (int i = 0; i < parts.Length; i++)
            {
                int row;
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out row))
                    continue;
                if (row < 1 || row > 4)
                    continue;
                _plateOn[(row - 1) * 2] = true;
                _plateOn[(row - 1) * 2 + 1] = true;
                any = true;
            }
            if (!any)
            {
                _plateOn[0] = true;
                _plateOn[1] = true;
            }
        }

        void ParsePlateSelection(string value)
        {
            _platesFromFile = true;
            for (int i = 0; i < _plateOn.Length; i++)
                _plateOn[i] = false;
            string[] parts = value.Split(',');
            bool any = false;
            for (int i = 0; i < parts.Length; i++)
            {
                int plate;
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out plate))
                    continue;
                if (plate < 1 || plate > 8)
                    continue;
                _plateOn[plate - 1] = true;
                any = true;
            }
            if (!any)
            {
                _plateOn[0] = true;
                _plateOn[1] = true;
            }
        }

        string StoredPlates()
        {
            var text = new StringBuilder();
            for (int i = 0; i < _plateOn.Length; i++)
            {
                if (!_plateOn[i])
                    continue;
                if (text.Length > 0)
                    text.Append(',');
                text.Append((i + 1).ToString(CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        void SaveTemplate()
        {
            try
            {
                File.WriteAllText(TemplatePath(), SettingsText(true));
            }
            catch (Exception ex)
            {
                AppendLog("Could not save plate positions: " + ex.Message);
            }
        }

        string SettingsText(bool session)
        {
            var text = new StringBuilder();
            text.AppendLine("DBA CNC scribe settings");
            text.AppendLine("version=2");
            text.AppendLine("kind=" + _fixtureKind);
            text.AppendLine("inches=" + (_fixtureInches ? "1" : "0"));
            text.AppendLine("p1top=" + FormatStored(_p1Top));
            text.AppendLine("p1bottom=" + FormatStored(_p1Bottom));
            text.AppendLine("p1right=" + FormatStored(_p1Right));
            text.AppendLine("p2top=" + FormatStored(_p2Top));
            text.AppendLine("p2bottom=" + FormatStored(_p2Bottom));
            text.AppendLine("p2right=" + FormatStored(_p2Right));
            text.AppendLine("p1z=" + (_zSet[0] ? _zValue[0].ToString("0.###", CultureInfo.InvariantCulture) : ""));
            text.AppendLine("p2z=" + (_zSet[1] ? _zValue[1].ToString("0.###", CultureInfo.InvariantCulture) : ""));
            for (int i = 0; i < _zSet.Length; i++)
                text.AppendLine("z" + (i + 1).ToString(CultureInfo.InvariantCulture) + "=" + (_zSet[i] ? _zValue[i].ToString("0.###", CultureInfo.InvariantCulture) : ""));
            for (int i = 0; i < _upperSet.Length; i++)
                text.AppendLine("set" + (i + 2).ToString(CultureInfo.InvariantCulture) + "=" + FormatStored(_upperSet[i]));
            text.AppendLine("plates=" + StoredPlates());
            text.AppendLine("depth=" + FormatDepth(ReadDepth()));
            text.AppendLine("lift=" + (_lift == null ? "" : _lift.Text.Trim()));
            FlushViewedProduct();
            text.AppendLine("product=" + (_rowProduct.Length == 0 ? "" : (_rowProduct[0] ?? "").Trim()));
            for (int i = 0; i < _rowProduct.Length; i++)
                text.AppendLine("product" + (i + 1).ToString(CultureInfo.InvariantCulture) + "=" + (_rowProduct[i] ?? "").Trim());
            text.AppendLine("applyAll=" + (_applyAll != null && _applyAll.Checked ? "1" : "0"));
            text.AppendLine("redo=" + (_redo != null && _redo.Checked ? "1" : "0"));
            text.AppendLine("redoLast=" + (_redoCount == null ? "3" : ((int)_redoCount.Value).ToString(CultureInfo.InvariantCulture)));
            text.AppendLine("feed=" + (_feed == null ? "" : _feed.Text.Trim()));
            text.AppendLine("display=" + (_display == null || _display.SelectedItem == null ? _savedDisplay : _display.SelectedItem.ToString()));
            if (session)
            {
                text.AppendLine("trueHome=" + (_trueHomeSet ? "1" : "0"));
                text.AppendLine("lastInches=" + (_lastInches ? "1" : "0"));
                text.AppendLine("last=" + (_haveLast
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", _lastX, _lastY, _lastZ)
                    : ""));
            }
            return text.ToString();
        }

        bool MotionBusy()
        {
            return _jogOutstanding || _motionQueue.Count > 0 || _scribeActive
                || (_link != null && _link.IsOpen && _state.Length > 0 && _state != "Idle");
        }

        void ExportSettings()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Export scribe settings";
                dialog.Filter = "DBA CNC settings (*.txt)|*.txt|All files (*.*)|*.*";
                dialog.FileName = "DBA CNC setup.txt";
                dialog.OverwritePrompt = true;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    File.WriteAllText(dialog.FileName, SettingsText(false));
                    AppendLog("Exported settings to " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    AppendLog("Could not export settings: " + ex.Message);
                }
            }
        }

        void ImportSettings()
        {
            if (MotionBusy())
            {
                AppendLog("Wait until the machine is idle before importing settings.");
                return;
            }
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Import scribe settings";
                dialog.Filter = "DBA CNC settings (*.txt)|*.txt|All files (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    ReadSettings(File.ReadAllLines(dialog.FileName));
                    CopyRightPlateFromLeft();
                    SaveTemplate();
                    RefreshPlateLabels();
                    RefreshPlateGrid();
                    SyncPlateCountBox();
                    RefreshPreview();
                    ApplySavedDisplay();
                    UpdateActions();
                    AppendLog("Imported settings from " + dialog.FileName + ". Set Home on the template mark before scribing. These positions are measured from that mark.");
                }
                catch (Exception ex)
                {
                    AppendLog("Could not import settings: " + ex.Message);
                }
            }
        }

        static string FormatStored(PlotPoint point)
        {
            if (!point.Set)
                return "";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", point.X, point.Y, point.Z);
        }

        static void ParsePoint(string value, PlotPoint point)
        {
            point.Set = false;
            if (value.Length == 0)
                return;
            string[] parts = value.Split(',');
            if (parts.Length != 3)
                return;
            double x;
            double y;
            double z;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x))
                return;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return;
            point.X = x;
            point.Y = y;
            point.Z = z;
            point.Set = true;
        }

        void ParseLast(string value)
        {
            _haveLast = false;
            if (value.Length == 0)
                return;
            string[] parts = value.Split(',');
            if (parts.Length != 3)
                return;
            double x;
            double y;
            double z;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x))
                return;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return;
            _lastX = x;
            _lastY = y;
            _lastZ = z;
            _haveLast = true;
        }

        void RememberPosition(bool force)
        {
            if (!_havePos || _suppressLastSave)
                return;
            bool nearZero = Math.Abs(_curX) < 0.05 && Math.Abs(_curY) < 0.05 && Math.Abs(_curZ) < 0.05;
            bool lastFar = _haveLast && (Math.Abs(_lastX) > 0.5 || Math.Abs(_lastY) > 0.5 || Math.Abs(_lastZ) > 0.5);
            if (nearZero && lastFar && !_atTrueHomeOnPurpose)
                return;
            bool changed = !_haveLast
                || Math.Abs(_curX - _lastX) > 0.01
                || Math.Abs(_curY - _lastY) > 0.01
                || Math.Abs(_curZ - _lastZ) > 0.01
                || _lastInches != _reportInches;
            if (!changed && !force)
                return;
            _haveLast = true;
            _lastX = _curX;
            _lastY = _curY;
            _lastZ = _curZ;
            _lastInches = _reportInches;
            if (force || (DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= 2)
            {
                _lastSaveUtc = DateTime.UtcNow;
                SaveTemplate();
            }
        }

        void RefreshTrueHomeLabel()
        {
            if (_trueHomeLabel == null)
                return;
            if (_homeSet)
                _trueHomeLabel.Text = "Home is set. Plot each plate from this mark.";
            else
                _trueHomeLabel.Text = "Jog to the template mark, then Set Home.";
        }

        void SetTrueHome()
        {
            if (!_link.IsOpen || !_havePos)
            {
                AppendLog("Connect and wait for a position before setting true home.");
                return;
            }
            if (_jogOutstanding || _motionQueue.Count > 0 || (_state.Length > 0 && _state != "Idle"))
            {
                AppendLog("Wait until the machine is idle.");
                return;
            }
            DialogResult answer = MessageBox.Show(this,
                "Are you sure you want to set home position?",
                "Set home",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
                return;
            _returnHomeStarted = true;
            _settingTrueHome = true;
            _motionQueue.Clear();
            string unit = _reportInches ? "G20" : "G21";
            int mask = _haveStatusMask ? _statusMask : 1;
            mask = mask | 2;
            _motionQueue.Add(unit);
            _motionQueue.Add("G90");
            _motionQueue.Add("G10 L20 P1 X0 Y0 Z0");
            _motionQueue.Add("$10=" + mask.ToString(CultureInfo.InvariantCulture));
            AppendLog("Setting home at the current position. X, Y, and Z will read 0.");
            PumpMotion();
        }

        void FinishSetTrueHome()
        {
            _settingTrueHome = false;
            _homeSet = true;
            _trueHomeSet = true;
            _atTrueHomeOnPurpose = true;
            _suppressLastSave = false;
            _haveLast = true;
            _lastX = 0;
            _lastY = 0;
            _lastZ = 0;
            _lastInches = _reportInches;
            double hx = 0;
            double hy = 0;
            double hz = 0;
            bool haveMachine = false;
            if (_haveMpos)
            {
                hx = _mposX;
                hy = _mposY;
                hz = _mposZ;
                haveMachine = true;
            }
            else if (_posKind == "MPos" && _havePos)
            {
                hx = _curX;
                hy = _curY;
                hz = _curZ;
                haveMachine = true;
            }
            bool platesSaved = _p1Top.Set || _p1Bottom.Set || _p1Right.Set || _p1ZSet
                || _p2Top.Set || _p2Bottom.Set || _p2Right.Set || _p2ZSet;
            for (int i = 0; i < _upperSet.Length; i++)
            {
                if (_upperSet[i].Set)
                    platesSaved = true;
            }
            for (int i = 0; i < _zSet.Length; i++)
            {
                if (_zSet[i])
                    platesSaved = true;
            }
            if (platesSaved && _fixtureKind == "MPos" && haveMachine)
            {
                ShiftPoint(_p1Top, hx, hy, hz);
                ShiftPoint(_p1Bottom, hx, hy, hz);
                ShiftPoint(_p1Right, hx, hy, hz);
                ShiftPoint(_p2Top, hx, hy, hz);
                ShiftPoint(_p2Bottom, hx, hy, hz);
                ShiftPoint(_p2Right, hx, hy, hz);
                for (int i = 0; i < _upperSet.Length; i++)
                    ShiftPoint(_upperSet[i], hx, hy, hz);
                for (int i = 0; i < _zSet.Length; i++)
                {
                    if (_zSet[i])
                        _zValue[i] -= hz;
                }
                if (_zSet[0])
                    _p1Z = _zValue[0];
                if (_zSet[1])
                    _p2Z = _zValue[1];
                _fixtureKind = "WPos";
                AppendLog("Saved plate positions now start from this home, X0 Y0 Z0.");
            }
            else if (platesSaved && _fixtureKind == "WPos")
            {
                _p1Top.Set = false;
                _p1Bottom.Set = false;
                _p1Right.Set = false;
                _p2Top.Set = false;
                _p2Bottom.Set = false;
                _p2Right.Set = false;
                for (int i = 0; i < _zSet.Length; i++)
                    ForgetZ(i);
                for (int i = 0; i < _upperSet.Length; i++)
                    _upperSet[i].Set = false;
                _fixtureKind = "WPos";
                _fixtureInches = _reportInches;
                AppendLog("Setting home moved the work zero, so the old plate positions were cleared. Plot both plates again.");
            }
            else if (!platesSaved)
            {
                _fixtureKind = "WPos";
                _fixtureInches = _reportInches;
            }
            if (haveMachine)
            {
                _workFromMachine = true;
                _homeMachineX = hx;
                _homeMachineY = hy;
                _homeMachineZ = hz;
            }
            _posKind = "WPos";
            _havePos = true;
            _curX = 0;
            _curY = 0;
            _curZ = 0;
            _posLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "X {0,9:0.000}     Y {1,9:0.000}     Z {2,9:0.000}", 0.0, 0.0, 0.0);
            UpdatePosMeta();
            RefreshPlateLabels();
            SaveTemplate();
            RefreshTrueHomeLabel();
            UpdateActions();
            AppendLog("Home is this spot: X0 Y0 Z0.");
        }

        static void ShiftPoint(PlotPoint point, double hx, double hy, double hz)
        {
            if (!point.Set)
                return;
            point.X -= hx;
            point.Y -= hy;
            point.Z -= hz;
        }

        void GoToTrueHome()
        {
            if (!_trueHomeSet)
            {
                AppendLog("Set true home before moving there.");
                return;
            }
            if (!_link.IsOpen || !_havePos)
            {
                AppendLog("Connect and wait for a position before moving.");
                return;
            }
            if (_jogOutstanding || _motionQueue.Count > 0 || (_state.Length > 0 && _state != "Idle"))
            {
                AppendLog("Wait until the machine is idle.");
                return;
            }
            AppendLog("Moving to true home.");
            BeginTravelToTrueHome(false, _curX, _curY, _curZ, _reportInches);
        }

        void MaybeReturnToTrueHome()
        {
            if (_returnHomeStarted || !_trueHomeSet || !_havePos || !_ready || !_unitsSeen)
                return;
            if (_jogOutstanding || _motionQueue.Count > 0 || _returnHomeActive || _settingTrueHome)
                return;
            if (_state == "Alarm" || _state == "Jog" || _state == "Run" || _state == "Hold" || _state == "Home" || _state == "Door")
                return;

            _returnHomeStarted = true;
            bool nearZero = Math.Abs(_curX) < 0.05 && Math.Abs(_curY) < 0.05 && Math.Abs(_curZ) < 0.05;
            bool nearLast = _haveLast
                && Math.Abs(_curX - _lastX) < 1.0
                && Math.Abs(_curY - _lastY) < 1.0
                && Math.Abs(_curZ - _lastZ) < 1.0;
            bool lastFar = _haveLast && (Math.Abs(_lastX) > 0.5 || Math.Abs(_lastY) > 0.5 || Math.Abs(_lastZ) > 0.5);

            if (nearZero && lastFar)
            {
                AppendLog("Controller reset its position to zero. Restoring the last real position, then moving to true home.");
                BeginTravelToTrueHome(true, _lastX, _lastY, _lastZ, _lastInches);
                return;
            }
            if (nearLast || !_haveLast)
            {
                AppendLog("Returning to true home.");
                BeginTravelToTrueHome(false, _curX, _curY, _curZ, _reportInches);
                return;
            }
            _suppressLastSave = false;
            AppendLog("Position does not match the last session. Jog to the reference mark and click Set True Home again.");
        }

        void BeginTravelToTrueHome(bool restoreLast, double poseX, double poseY, double poseZ, bool inches)
        {
            _returnHomeActive = true;
            double feed = ReadFeed();
            if (inches)
                feed = feed / 25.4;
            if (feed < 0.1)
                feed = 0.1;
            string unit = inches ? "G20" : "G21";
            double lift = ReadLift();
            if (inches)
                lift = lift / 25.4;

            if (restoreLast)
            {
                _motionQueue.Add(unit);
                _motionQueue.Add(string.Format(CultureInfo.InvariantCulture,
                    "G10 L20 P1 X{0:0.###} Y{1:0.###} Z{2:0.###}", poseX, poseY, poseZ));
            }
            if (poseZ < lift - 0.02)
            {
                _motionQueue.Add(string.Format(CultureInfo.InvariantCulture,
                    "$J=G91 {0} Z{1:0.###} F{2:0.##}", unit, lift - poseZ, feed));
                AppendLog("Raising Z clear of the plates before traveling home.");
            }
            if (Math.Abs(poseX) > 0.2)
            {
                _motionQueue.Add(string.Format(CultureInfo.InvariantCulture,
                    "$J=G90 {0} X0 F{1:0.##}", unit, feed));
            }
            if (Math.Abs(poseY) > 0.2)
            {
                _motionQueue.Add(string.Format(CultureInfo.InvariantCulture,
                    "$J=G90 {0} Y0 F{1:0.##}", unit, feed));
            }
            if (_motionQueue.Count == 0)
            {
                _returnHomeActive = false;
                _suppressLastSave = false;
                _atTrueHomeOnPurpose = true;
                AppendLog("Already at true home.");
                UpdateActions();
                return;
            }
            PumpMotion();
        }

        void SendStop()
        {
            bool scribe = _scribeActive;
            _motionQueue.Clear();
            _plateMoveActive = false;
            _returnHomeActive = false;
            _settingTrueHome = false;
            _suppressLastSave = false;
            _loadActive = false;
            _scribeActive = false;
            _jobPaused = false;
            _jogOutstanding = false;
            if (!_link.IsOpen)
                return;
            try
            {
            if (scribe)
            {
                StopJobTimer();
                _link.WriteRealtime(SoftReset);
                    AppendLog("Scribe stopped. The bit may still be down. Jog Z up when the controller is idle.");
                    return;
                }
                if (_state == "Home")
                {
                    _link.WriteRealtime(SoftReset);
                    AppendLog("Stop. Reset sent to abort homing.");
                }
                else if (_state == "Run")
                {
                    _link.WriteRealtime(FeedHold);
                    AppendLog("Feed hold. Press Resume to continue.");
                }
                else
                {
                    _link.WriteRealtime(JogCancel);
                    AppendLog("Stop.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Stop failed: " + ex.Message);
            }
            UpdateActions();
        }

        void SendPause()
        {
            if (!_link.IsOpen)
            {
                AppendLog("Connect before pausing.");
                return;
            }
            if (_state == "Hold" || _jobPaused)
            {
                AppendLog("Already paused. Press Resume to continue.");
                return;
            }
            bool running = _state == "Run" || _state == "Jog" || _state == "Home"
                || _scribeActive || _motionQueue.Count > 0 || _jogOutstanding;
            if (!running)
            {
                AppendLog("Nothing is moving to pause.");
                return;
            }
            _jobPaused = true;
            FreezeJobTimer();
            try
            {
                if (_state == "Run" || _state == "Home")
                {
                    _link.WriteRealtime(FeedHold);
                    AppendLog("Paused. Resume continues from this spot. Stop aborts the job.");
                }
                else if (_state == "Jog")
                {
                    _link.WriteRealtime(JogCancel);
                    AppendLog("Jog paused.");
                }
                else
                    AppendLog("Paused. Resume sends the rest of the moves.");
            }
            catch (Exception ex)
            {
                AppendLog("Pause failed: " + ex.Message);
            }
            UpdateActions();
        }

        void SendResume()
        {
            if (!_link.IsOpen)
                return;
            bool held = _state == "Hold";
            bool waiting = _jobPaused && (_motionQueue.Count > 0 || _scribeActive);
            if (!held && !waiting)
            {
                AppendLog("The machine is not paused.");
                return;
            }
            _jobPaused = false;
            ThawJobTimer();
            try
            {
                if (held)
                    _link.WriteRealtime(CycleStart);
                AppendLog("Resume.");
                if (_motionQueue.Count > 0)
                    PumpMotion();
            }
            catch (Exception ex)
            {
                AppendLog("Resume failed: " + ex.Message);
            }
            UpdateActions();
        }

        void SendUnlock()
        {
            if (!_link.IsOpen)
                return;
            if (_state != "Alarm")
            {
                AppendLog("There is no alarm to clear.");
                return;
            }
            DialogResult answer = MessageBox.Show(this,
                "Unlock clears an alarm so the machine can move.\r\n\r\nIt does not fix a limit switch that is still tripped. Continue only if the machine is clear.",
                "Unlock alarm",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
                return;
            try
            {
                _link.WriteLine("$X");
                AppendLog("Unlock ($X).");
            }
            catch (Exception ex)
            {
                AppendLog("Unlock failed: " + ex.Message);
            }
        }

        void SendHome()
        {
            if (!_link.IsOpen)
                return;
            DialogResult answer = MessageBox.Show(this,
                "Homing drives each axis until it hits a home switch.\r\n\r\nOnly continue if this CNC is set up for homing. Stay at the machine and press Stop if an axis moves the wrong way.",
                "Home the machine",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
                return;
            try
            {
                _link.WriteLine("$H");
                AppendLog("Homing ($H).");
            }
            catch (Exception ex)
            {
                AppendLog("Home failed: " + ex.Message);
            }
        }

        void HandleFault(string message)
        {
            if (_closing)
                return;
            Disconnect("Connection lost: " + message);
        }

        void HandleLine(string line)
        {
            if (_closing || !_link.IsOpen)
                return;

            string state;
            string kind;
            double x;
            double y;
            double z;
            if (TryParseStatus(line, out state, out kind, out x, out y, out z))
            {
                CaptureMachine(line);
                ApplyStatus(state, kind, x, y, z);
                MarkReady();
                return;
            }

            if (line == "ok")
            {
                _jogOutstanding = false;
                if (_jobPaused)
                {
                    UpdateActions();
                    return;
                }
                if (_motionQueue.Count > 0)
                {
                    PumpMotion();
                    return;
                }
                if (_plateMoveActive)
                {
                    _plateMoveActive = false;
                    AppendLog("Plate move finished. The bit should be above that plate's top-left corner.");
                }
                if (_returnHomeActive)
                {
                    _returnHomeActive = false;
                    _suppressLastSave = false;
                    _atTrueHomeOnPurpose = true;
                    AppendLog("Back at home X0 Y0 Z0.");
                    RememberPosition(true);
                }
                if (_settingTrueHome)
                {
                    FinishSetTrueHome();
                }
                if (_scribeActive)
                {
                    _scribeActive = false;
                    StopJobTimer();
                    AppendLog("Scribe finished. Back at home X0 Y0 Z0.");
                }
                if (_loadActive)
                {
                    _loadActive = false;
                    AppendLog("Ready to load plates. Z is 15 mm above home.");
                }
                MarkReady();
                UpdateActions();
                return;
            }

            if (line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                _motionQueue.Clear();
                _plateMoveActive = false;
                _returnHomeActive = false;
                _settingTrueHome = false;
                _suppressLastSave = false;
                _loadActive = false;
                _jobPaused = false;
                if (_scribeActive)
                    StopJobTimer();
                _scribeActive = false;
                _jogOutstanding = false;
                AppendLog(ExplainError(line));
                MarkReady();
                UpdateActions();
                return;
            }

            if (line.StartsWith("ALARM", StringComparison.OrdinalIgnoreCase))
            {
                _motionQueue.Clear();
                _plateMoveActive = false;
                _returnHomeActive = false;
                _settingTrueHome = false;
                _suppressLastSave = false;
                _loadActive = false;
                _jobPaused = false;
                if (_scribeActive)
                    StopJobTimer();
                _scribeActive = false;
                _jogOutstanding = false;
                _state = "Alarm";
                _stateLabel.Text = "Alarm";
                _stateLabel.ForeColor = StateColor("Alarm");
                AppendLog(ExplainAlarm(line));
                MarkReady();
                UpdateActions();
                return;
            }

            if (line.StartsWith("$13="))
            {
                _reportInches = line.StartsWith("$13=1");
                _unitsSeen = true;
                if (!_loggedUnits)
                {
                    _loggedUnits = true;
                    AppendLog(_reportInches
                        ? "Position reports are in inches. Jog steps are still millimeters."
                        : "Position reports are in millimeters.");
                }
                UpdatePosMeta();
                return;
            }

            if (line.StartsWith("$10="))
            {
                string raw = line.Substring(4).Trim();
                int space = raw.IndexOf(' ');
                if (space > 0)
                    raw = raw.Substring(0, space);
                int mask;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask))
                {
                    _statusMask = mask;
                    _haveStatusMask = true;
                }
            }

            if (line.StartsWith("$132="))
            {
                double travel;
                string raw = line.Substring(5).Trim();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out travel))
                {
                    _zTravel = Math.Abs(travel);
                    _haveZTravel = _zTravel > 1;
                }
            }

            if (line.StartsWith("$"))
                return;

            if (line.StartsWith("Grbl ", StringComparison.Ordinal))
            {
                if (_firmwareLabel.Text.Length == 0)
                    _firmwareLabel.Text = line;
                AppendLog(line);
                MarkReady();
                return;
            }

            if (line.StartsWith("[VER:", StringComparison.OrdinalIgnoreCase))
            {
                _firmwareLabel.Text = CleanVersion(line);
                AppendLog(line);
                MarkReady();
                return;
            }

            if (line.StartsWith("["))
            {
                AppendLog(line);
                MarkReady();
                return;
            }

            if (HasLetter(line) && !MostlyBinary(line))
                AppendLog("Controller: " + line);
        }

        void ApplyStatus(string state, string kind, double x, double y, double z)
        {
            _state = state;
            if (kind == "WPos")
                _workFromMachine = false;
            else if (kind == "MPos" && _workFromMachine)
            {
                x -= _homeMachineX;
                y -= _homeMachineY;
                z -= _homeMachineZ;
                kind = "WPos";
            }
            if (kind.Length > 0)
            {
                _posKind = kind;
                _havePos = true;
                _curX = x;
                _curY = y;
                _curZ = z;
            }
            _stateLabel.Text = state;
            _stateLabel.ForeColor = StateColor(state);
            _posLabel.Text = string.Format(CultureInfo.InvariantCulture,
                "X {0,9:0.000}     Y {1,9:0.000}     Z {2,9:0.000}", x, y, z);
            UpdatePosMeta();
            if (state == "Alarm")
                _jogOutstanding = false;
            if (!_suppressLastSave)
                RememberPosition(false);
            UpdateActions();
        }

        void UpdatePosMeta()
        {
            if (_posKind.Length == 0)
                return;
            string where = _posKind == "WPos" ? "Work position" : "Machine position";
            string unit = _reportInches ? "inches" : "mm";
            _posMeta.Text = where + ", reported in " + unit + ". Jog steps are mm.";
        }

        void MarkReady()
        {
            if (_ready)
                return;
            _ready = true;
            AppendLog("Controller is responding.");
            UpdateActions();
        }

        void UpdateActions()
        {
            bool open = _link.IsOpen;
            bool settled = _motionQueue.Count == 0 && !_plateMoveActive && !_scribeActive && !_jogOutstanding && (_state.Length == 0 || _state == "Idle");
            bool pausedIdle = _jobPaused && !_jogOutstanding && (_state == "Idle" || _state.Length == 0);
            bool canJog = open && _ready && (pausedIdle || settled);
            bool canPlot = canJog && _havePos;
            bool canTeach = canPlot && _homeSet;
            bool teachBase = canTeach && _viewRow == 0;
            _plot1Top.Enabled = canTeach;
            _plot1Bottom.Enabled = teachBase;
            _plot1Right.Enabled = teachBase;
            _plot1Z.Enabled = canTeach;
            _plot2Top.Enabled = teachBase;
            _plot2Z.Enabled = canTeach;
            PlotPoint shownLeft;
            PlotPoint shownBottom;
            PlotPoint shownRight;
            bool shownZSet;
            double shownZ;
            DisplayedSide(true, out shownLeft, out shownBottom, out shownRight, out shownZSet, out shownZ);
            PlotPoint shownRightTop;
            DisplayedSide(false, out shownRightTop, out shownBottom, out shownRight, out shownZSet, out shownZ);
            _goPlate1.Enabled = canPlot && shownLeft.Set;
            _goPlate2.Enabled = canPlot && shownRightTop.Set;
            PaintLive(_setTrueHome, canPlot && !_scribeActive);
            PaintAction(_goHome);
            PaintAction(_loadPlates);
            PaintAction(_goScribe);
            PaintAction(_pauseJob);
            PaintAction(_resume);
            PaintAction(_unlock);
            if (_setPlot != null)
            {
                for (int i = 0; i < _setPlot.Length; i++)
                {
                    if (_setPlot[i] != null)
                        _setPlot[i].Enabled = canTeach;
                }
            }
            PaintLive(_xPlus, canJog);
            PaintLive(_xMinus, canJog);
            PaintLive(_yPlus, canJog);
            PaintLive(_yMinus, canJog);
            PaintLive(_zPlus, canJog);
            PaintLive(_zMinus, canJog);
            PaintLive(_stop, open);
        }

        void CaptureMachine(string line)
        {
            foreach (Match match in PosPattern.Matches(line))
            {
                if (match.Groups[1].Value != "MPos")
                    continue;
                _haveMpos = true;
                _mposX = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                _mposY = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                _mposZ = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            }
        }

        static bool TryParseStatus(string line, out string state, out string kind, out double x, out double y, out double z)
        {
            state = "";
            kind = "";
            x = 0;
            y = 0;
            z = 0;
            Match status = StatusPattern.Match(line.Trim());
            if (!status.Success)
                return false;
            state = status.Groups[1].Value.Trim();
            string body = status.Groups[2].Success ? status.Groups[2].Value : "";
            Match chosen = null;
            foreach (Match match in PosPattern.Matches(body.Length > 0 ? body : line))
            {
                if (chosen == null || match.Groups[1].Value == "WPos")
                    chosen = match;
            }
            if (chosen == null)
                return true;
            kind = chosen.Groups[1].Value;
            x = double.Parse(chosen.Groups[2].Value, CultureInfo.InvariantCulture);
            y = double.Parse(chosen.Groups[3].Value, CultureInfo.InvariantCulture);
            z = double.Parse(chosen.Groups[4].Value, CultureInfo.InvariantCulture);
            return true;
        }

        static string ExplainError(string line)
        {
            string code = line.Substring(line.IndexOf(':') + 1).Trim();
            if (code == "8")
                return "Controller refused the command because it is not idle.";
            if (code == "9")
                return "Locked. The machine is in an alarm. Check it, then use Unlock if it is safe.";
            if (code == "15")
                return "That jog was ignored because it would pass a travel limit.";
            return "Controller returned " + line + ".";
        }

        static string ExplainAlarm(string line)
        {
            int colon = line.IndexOf(':');
            string code = colon >= 0 ? line.Substring(colon + 1).Trim() : "";
            if (code == "1")
                return "Alarm 1: a limit switch is tripped.";
            if (code == "2")
                return "Alarm 2: a move hit a soft travel limit.";
            if (code == "3")
                return "Alarm 3: motion was aborted.";
            if (code == "6" || code == "7" || code == "8" || code == "9")
                return "Alarm " + code + ": homing failed. Check the switches and the direction of travel.";
            return "Alarm lock (" + line + "). Check the machine before Unlock.";
        }

        static string CleanVersion(string line)
        {
            string inner = line.Trim();
            if (inner.StartsWith("[") && inner.EndsWith("]"))
                inner = inner.Substring(1, inner.Length - 2);
            if (inner.StartsWith("VER:"))
                inner = inner.Substring(4);
            while (inner.Length > 0 && inner[inner.Length - 1] == ':')
                inner = inner.Substring(0, inner.Length - 1);
            return "Grbl " + inner.Trim();
        }

        static bool HasLetter(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetter(c))
                    return true;
            }
            return false;
        }

        static bool MostlyBinary(string text)
        {
            int bad = 0;
            foreach (char c in text)
            {
                if (c < 32 || c > 126)
                    bad++;
            }
            return text.Length > 0 && bad * 3 > text.Length;
        }

        static Color StateColor(string state)
        {
            if (state == "Idle")
                return Ink;
            if (state == "Jog" || state == "Run")
                return Brand;
            if (state == "Alarm")
                return Brand;
            if (state == "Hold" || state == "Home")
                return Brand;
            return Ink;
        }

        bool PreFilterKey(ref Message message)
        {
            const int WM_KEYDOWN = 0x100;
            const int WM_SYSKEYDOWN = 0x104;
            if (message.Msg != WM_KEYDOWN && message.Msg != WM_SYSKEYDOWN)
                return false;
            if (!ContainsFocus)
                return false;

            Keys key = (Keys)(message.WParam.ToInt32() & 0xFFFF);
            if (key == Keys.Escape)
            {
                SendStop();
                return true;
            }

            if (LetFocusedControlUseKey(key))
                return false;

            string axis;
            int sign;
            if (!TryMapKey(key, out axis, out sign))
                return false;

            int flags = message.LParam.ToInt32();
            bool repeated = (flags & (1 << 30)) != 0;
            SendJog(axis, sign, !repeated);
            return true;
        }

        bool LetFocusedControlUseKey(Keys key)
        {
            ComboBox combo = ActiveControl as ComboBox;
            if (combo != null && combo.DroppedDown)
                return true;
            if (ActiveControl == _depth || ActiveControl == _plateCount || ActiveControl == _redoCount || ActiveControl == _display)
                return true;
            if ((ActiveControl == _feed || ActiveControl == _product) && (key == Keys.Left || key == Keys.Right))
                return true;
            return false;
        }

        static bool TryMapKey(Keys key, out string axis, out int sign)
        {
            axis = "";
            sign = 0;
            if (key == Keys.Right) { axis = "X"; sign = 1; return true; }
            if (key == Keys.Left) { axis = "X"; sign = -1; return true; }
            if (key == Keys.Up) { axis = "Y"; sign = 1; return true; }
            if (key == Keys.Down) { axis = "Y"; sign = -1; return true; }
            if (key == Keys.PageUp || key == Keys.Prior) { axis = "Z"; sign = 1; return true; }
            if (key == Keys.Next) { axis = "Z"; sign = -1; return true; }
            return false;
        }

        void PaintPreview(object sender, PaintEventArgs e)
        {
            Panel panel = (Panel)sender;
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            using (Pen border = new Pen(Color.FromArgb(190, 198, 208)))
                g.DrawRectangle(border, 0, 0, panel.Width - 1, panel.Height - 1);

            string digits = PreviewDigits();
            using (Font captionFont = new Font("Segoe UI", 8f))
            using (Brush muted = new SolidBrush(Color.FromArgb(70, 80, 92)))
            {
                string caption = "Row " + (_viewRow + 1).ToString(CultureInfo.InvariantCulture)
                    + "   " + FormatDepth(ReadDepth()) + " mm";
                if (_redo != null && _redo.Checked && _redoCount != null)
                {
                    int last = (int)_redoCount.Value;
                    if (last > digits.Length)
                        caption += "   re-do is too long";
                    else if (last < digits.Length)
                        caption += "   re-do last " + last.ToString(CultureInfo.InvariantCulture);
                }
                g.DrawString(caption, captionFont, muted, 8, 4);
            }

            if (digits.Length == 0)
            {
                using (Font font = new Font("Segoe UI", 9f))
                using (Brush hint = new SolidBrush(Color.FromArgb(140, 148, 158)))
                    g.DrawString("This row's text appears here.", font, hint, 8, 26);
                return;
            }

            double plateH = 6;
            double plateW = 40;
            if (_p1Top.Set && _p1Bottom.Set && _p1Right.Set)
            {
                plateH = Math.Abs(_p1Top.Y - _p1Bottom.Y);
                plateW = Math.Abs(_p1Right.X - _p1Top.X);
                if (plateH < 0.1)
                    plateH = 0.1;
                if (plateW < 0.1)
                    plateW = 0.1;
            }

            float pad = 8f;
            float header = 22f;
            float availW = panel.Width - pad * 2f;
            float availH = panel.Height - header - 8f;
            if (availW < 10f || availH < 10f)
                return;

            float boxW = availW;
            float boxH = (float)(boxW * plateH / plateW);
            if (boxH > availH)
            {
                boxH = availH;
                boxW = (float)(boxH * plateW / plateH);
            }
            float boxX = pad + (availW - boxW) / 2f;
            float boxY = header + (availH - boxH) / 2f;
            using (Brush fill = new SolidBrush(Color.FromArgb(236, 240, 244)))
                g.FillRectangle(fill, boxX, boxY, boxW, boxH);
            using (Pen edge = new Pen(Ink))
                g.DrawRectangle(edge, boxX, boxY, boxW, boxH);

            int count = digits.Length;
            int redoFrom = RedoStart(count);
            double charH;
            double charW;
            double gap;
            FitGlyph(count, plateH, plateW, out charH, out charW, out gap);

            using (Pen kept = new Pen(Color.FromArgb(186, 194, 204), 2f))
            using (Pen stroke = new Pen(Color.FromArgb(24, 32, 44), 2f))
            {
                kept.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                kept.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                stroke.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                stroke.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                for (int i = 0; i < count; i++)
                {
                    double along = i * (charW + gap);
                    string segs = Glyph(digits[i]);
                    Pen pen = i < redoFrom ? kept : stroke;
                    for (int s = 0; s < segs.Length; s++)
                    {
                        double u1;
                        double v1;
                        double u2;
                        double v2;
                        SegmentEnds(segs[s], out u1, out v1, out u2, out v2);
                        float x1 = boxX + (float)((along + u1 * charW) / plateW * boxW);
                        float y1 = boxY + boxH - (float)((v1 * charH) / plateH * boxH);
                        float x2 = boxX + (float)((along + u2 * charW) / plateW * boxW);
                        float y2 = boxY + boxH - (float)((v2 * charH) / plateH * boxH);
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                }
            }
        }

        string PreviewDigits()
        {
            string raw = _product == null || _product.Text == null ? "" : _product.Text;
            var kept = new StringBuilder();
            for (int i = 0; i < raw.Length; i++)
            {
                char keptChar;
                if (TryKeepProductChar(raw[i], out keptChar))
                    kept.Append(keptChar);
            }
            return kept.ToString();
        }

        sealed class PreviewPanel : Panel
        {
            public PreviewPanel()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = Color.White;
            }
        }

        sealed class JogKeyFilter : IMessageFilter
        {
            readonly MainForm _form;

            public JogKeyFilter(MainForm form)
            {
                _form = form;
            }

            public bool PreFilterMessage(ref Message message)
            {
                return _form.PreFilterKey(ref message);
            }
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _generation++;
            _statusTimer.Stop();
            FlushViewedProduct();
            SaveTemplate();
            RememberPosition(true);
            try
            {
                if (_link.IsOpen)
                    _link.Close();
            }
            catch
            {
            }
        }

        void AppendLog(string message)
        {
            if (string.IsNullOrEmpty(message) || _log == null)
                return;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            if (_log.TextLength > 40000)
                _log.Text = _log.Text.Substring(_log.TextLength - 20000);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        void Ui(Action action)
        {
            if (!IsHandleCreated || IsDisposed)
                return;
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
