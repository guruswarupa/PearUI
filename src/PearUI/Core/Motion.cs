using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PearUI.Core;

/// <summary>
/// Shared animation helpers, built so motion looks the same, and smooth, on a 60 Hz laptop and a 240 Hz monitor:
/// everything is driven by elapsed time, never by "per frame" amounts.
/// </summary>
internal static class Motion
{
    // ---------- frame rate ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

    /// <summary>The refresh rate of the fastest attached display (60 when it can't be read).</summary>
    public static int MaxRefreshRate()
    {
        int hz = 60;
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(screen.DeviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm) && dm.dmDisplayFrequency > 1)
                    hz = Math.Max(hz, (int)dm.dmDisplayFrequency);
            }
        }
        catch { }
        return Math.Clamp(hz, 60, 360);
    }

    /// <summary>
    /// WPF animations run at 60 frames a second unless told otherwise, which looks steppy on 120, 144 or 240 Hz screens.
    /// Call once at start-up, before any animation is created.
    /// </summary>
    public static void Init()
    {
        try
        {
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata((int?)MaxRefreshRate()));
        }
        catch { }
    }

    // ---------- start after the first frame ----------
    /// <summary>
    /// Runs <paramref name="start"/> once the next frame is about to be drawn. Animations started before a window's
    /// first layout lose their first moments to that work and appear to jump; started here, they begin on time.
    /// </summary>
    public static void OnNextFrame(Action start)
    {
        bool done = false;
        EventHandler handler = null;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        void Run()
        {
            if (done) return;
            done = true;
            CompositionTarget.Rendering -= handler;
            fallback.Stop();
            start();
        }
        handler = (_, _) => Run();
        fallback.Tick += (_, _) => Run();
        CompositionTarget.Rendering += handler;
        fallback.Start();
    }

    // ---------- spring ----------
    /// <summary>
    /// Moves a value to a target like a critically damped spring: it keeps the speed it already had (so a flick carries on
    /// smoothly), arrives quickly without bouncing, and can be redirected at any moment. Time-based, so frame-rate independent.
    /// </summary>
    internal sealed class Spring
    {
        private readonly Action<double> _apply;
        private readonly Stopwatch _clock = new();
        private double _a, _b, _target, _omega;
        private Action _done;
        private bool _running;

        public Spring(Action<double> apply) { _apply = apply; }

        public bool IsRunning => _running;

        /// <summary>The value and speed (units per second) as of the last frame.</summary>
        public double Value { get; private set; }
        public double Velocity { get; private set; }

        /// <param name="velocity">Units per second, in the direction of travel.</param>
        /// <param name="omega">Stiffness: about 6 / omega seconds to settle (22 = roughly a quarter of a second).</param>
        public void Start(double from, double to, double velocity = 0, double omega = 22, Action done = null)
        {
            _target = to;
            _omega = omega;
            Value = from;
            Velocity = velocity;
            _a = from - to;
            _b = velocity + omega * _a;
            _done = done;
            _clock.Restart();
            if (!_running)
            {
                _running = true;
                CompositionTarget.Rendering += Tick;
            }
        }

        /// <summary>
        /// Aims at a new target without any jump: from where the value is now, at the speed it has now. Called on every input event
        /// (for example each touchpad movement), it makes the value follow the input smoothly, even when input arrives less often than frames are drawn.
        /// </summary>
        public void Retarget(double to, double omega, double valueIfIdle)
        {
            if (_running) Start(Value, to, Velocity, omega, _done);
            else Start(valueIfIdle, to, 0, omega, _done);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= Tick;
        }

        private void Tick(object sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds;
            double decay = Math.Exp(-_omega * t);
            double x = (_a + _b * t) * decay;                       // distance from the target
            double v = (_b - _omega * (_a + _b * t)) * decay;       // its speed
            if (t > 0.04 && Math.Abs(x) < 0.3 && Math.Abs(v) < 6)
            {
                Stop();
                Value = _target;
                Velocity = 0;
                _apply(_target);
                var done = _done;
                _done = null;
                done?.Invoke();
                return;
            }
            Value = _target + x;
            Velocity = v;
            _apply(Value);
        }
    }
}
