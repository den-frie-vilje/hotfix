using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ComputeSharp;

namespace Hotfix;

/// <summary>
/// The two burners and the watcher that keeps them out of your way. Each half yields twice over, on its own:
/// <list type="bullet">
/// <item>By priority, at once: the CPU threads run at idle priority and the process asks the WDDM scheduler for
/// idle priority on the GPU, so your work always goes first.</item>
/// <item>By turning down: a chip kept hot while you work clocks lower and spends its turbo budget, so priority
/// alone still costs you speed. Each half's heat follows the headroom other apps leave on that part: full while
/// they are quiet, none while they are busy, in proportion between. It drops at once when your work picks up
/// and climbs back over ten seconds when it eases.</item>
/// </list>
/// </summary>
static class Heater
{
    static readonly (double Quiet, double Busy) CpuLoad = (0.10, 0.35); // other apps' share of all logical processors
    static readonly (double Quiet, double Busy) GpuLoad = (5, 40); // other apps' use of the busiest GPU engine, per cent
    const double Climb = 0.05; // per half-second tick, so ten seconds from nothing to full
    const int GpuWindowMs = 100; // the GPU burns for its share of each window and rests for the rest

    static readonly object sync = new();
    static readonly ManualResetEventSlim gpuGate = new();
    static ManualResetEventSlim[] cpuGates = [];
    static bool cpuWanted, gpuWanted;
    static double cpuRoom = 1, gpuRoom = 1, cpuHeat, gpuHeat;
    static float sink; // keeps the JIT from dropping the CPU loop

    public static GraphicsDevice? Device { get; private set; }
    public static bool GpuIdle { get; private set; }

    /// <summary>How hard each half is warming right now, from 0 to 1.</summary>
    public static (double Cpu, double Gpu) Heat
    {
        get { lock (sync) return (cpuHeat, gpuHeat); }
    }

    public static void Start()
    {
        using (var self = Process.GetCurrentProcess())
            GpuIdle = D3DKMTSetProcessSchedulingPriorityClass(self.Handle, IdleGpuClass) == 0;
        try { Device = GraphicsDevice.GetDefault(); } catch (Exception) { } // no DirectX 12 GPU: CPU only

        cpuGates = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => new ManualResetEventSlim()).ToArray();
        foreach (var gate in cpuGates)
            new Thread(() => BurnCpu(gate)) { IsBackground = true, Name = "Hotfix CPU" }.Start();
        // Above the CPU burners so they never starve the GPU of work; it spends its time waiting on the GPU anyway.
        if (Device is { } device)
            new Thread(() => BurnGpu(device)) { IsBackground = true, Name = "Hotfix GPU", Priority = ThreadPriority.Lowest }.Start();
        new Thread(Watch) { IsBackground = true, Name = "Hotfix watcher" }.Start();
    }

    /// <summary>What the person asked for. It takes effect at once, at whatever the headroom allows.</summary>
    public static void Want(bool cpu, bool gpu)
    {
        lock (sync)
        {
            cpuWanted = cpu;
            gpuWanted = gpu && Device != null;
            cpuHeat = cpuWanted ? cpuRoom : 0;
            gpuHeat = gpuWanted ? gpuRoom : 0;
            Apply();
        }
    }

    static void Apply()
    {
        int burning = (int)Math.Round(cpuHeat * cpuGates.Length);
        for (int i = 0; i < cpuGates.Length; i++)
            Toggle(cpuGates[i], i < burning);
        Toggle(gpuGate, gpuHeat > 0);
    }

    static void Toggle(ManualResetEventSlim gate, bool on)
    {
        if (on) gate.Set(); else gate.Reset();
    }

    static double Room(double others, (double Quiet, double Busy) load) =>
        Math.Clamp((load.Busy - others) / (load.Busy - load.Quiet), 0, 1);

    static double Follow(double heat, double target) => target < heat ? target : Math.Min(target, heat + Climb);

    static void Watch()
    {
        using var self = Process.GetCurrentProcess();
        var gpu = new GpuUsage();
        GetSystemTimes(out long idle0, out long kernel0, out long user0);
        long own0 = self.TotalProcessorTime.Ticks;
        while (true)
        {
            Thread.Sleep(500);
            GetSystemTimes(out long idle, out long kernel, out long user);
            self.Refresh();
            long own = self.TotalProcessorTime.Ticks;
            long total = kernel - kernel0 + user - user0; // kernel time includes idle time
            double cpuOthers = total > 0 ? (double)(total - (idle - idle0) - (own - own0)) / total : 0;
            (idle0, kernel0, user0, own0) = (idle, kernel, user, own);
            double gpuOthers = gpu.Others();

            lock (sync)
            {
                cpuRoom = Room(cpuOthers, CpuLoad);
                gpuRoom = Room(gpuOthers, GpuLoad);
                cpuHeat = Follow(cpuHeat, cpuWanted ? cpuRoom : 0);
                gpuHeat = Follow(gpuHeat, gpuWanted ? gpuRoom : 0);
                Apply();
            }
        }
    }

    static void BurnCpu(ManualResetEventSlim gate)
    {
        SetThreadPriority(GetCurrentThread(), IdleThreadPriority);
        // Eight independent AVX FMA chains keep both FMA ports busy, all in registers, so caches stay yours. Each
        // starts on the answer, so every multiply-add returns exactly the number it was given (see Burn).
        var m = Vector256.Create(Burn.Half);
        var c = Vector256.Create(Burn.Tiny);
        var a = Vector256.Create(Burn.Answer);
        Vector256<float> b = a, d = a, e = a, f = a, g = a, h = a, k = a;
        while (true)
        {
            gate.Wait();
            for (int i = 0; i < 1_000_000; i++) // about 1 ms, so stopping is instant
            {
                a = Vector256.FusedMultiplyAdd(a, m, c);
                b = Vector256.FusedMultiplyAdd(b, m, c);
                d = Vector256.FusedMultiplyAdd(d, m, c);
                e = Vector256.FusedMultiplyAdd(e, m, c);
                f = Vector256.FusedMultiplyAdd(f, m, c);
                g = Vector256.FusedMultiplyAdd(g, m, c);
                h = Vector256.FusedMultiplyAdd(h, m, c);
                k = Vector256.FusedMultiplyAdd(k, m, c);
            }
            sink = (a + b + d + e + f + g + h + k).GetElement(0);
        }
    }

    /// <summary>The GPU's working buffer, one float per thread, starting on the answer.</summary>
    internal static ReadWriteBuffer<float> NewBuffer(GraphicsDevice device) =>
        device.AllocateReadWriteBuffer(Enumerable.Repeat(Burn.Answer, 1 << 21).ToArray());

    static void BurnGpu(GraphicsDevice device)
    {
        using var buffer = NewBuffer(device);
        var window = new Stopwatch();
        while (true)
        {
            gpuGate.Wait();
            double share = Heat.Gpu;
            window.Restart();
            do device.For(buffer.Length, new Burn(buffer)); // a few ms per dispatch, so other work slots in between
            while (window.ElapsedMilliseconds < GpuWindowMs * share);
            int rest = GpuWindowMs - (int)window.ElapsedMilliseconds;
            if (rest > 0) Thread.Sleep(rest);
        }
    }

    /// <summary>
    /// How busy other apps keep the GPU: the busiest 3D or compute engine's utilisation, from the same
    /// per-process counters Task Manager shows, leaving out Hotfix's own.
    /// </summary>
    sealed class GpuUsage
    {
        readonly IntPtr query, counter;
        readonly string own = $"pid_{Environment.ProcessId}_";

        public GpuUsage()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0
                && PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) == 0)
                PdhCollectQueryData(query); // rates need a first sample
            else
                counter = IntPtr.Zero; // no counters: the GPU half never turns down on usage, priority still applies
        }

        public double Others()
        {
            if (counter == IntPtr.Zero || PdhCollectQueryData(query) != 0) return 0;
            int size = 0;
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA) return 0;
            var items = Marshal.AllocHGlobal(size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out int count, items) != 0) return 0;
                // Instances look like pid_1234_luid_0x0_0x12896_phys_0_eng_0_engtype_3D; sum per engine, across processes.
                var engines = new Dictionary<string, double>();
                for (int i = 0; i < count; i++)
                {
                    var item = items + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name, status, padding, double
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item))!;
                    if (name.StartsWith(own) || Marshal.ReadInt32(item + 8) > 1 || !(name.EndsWith("engtype_3D") || name.Contains("engtype_Compute"))) continue;
                    string engine = name[name.IndexOf("_luid_")..];
                    engines[engine] = engines.GetValueOrDefault(engine) + BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16));
                }
                return engines.Count > 0 ? engines.Values.Max() : 0;
            }
            finally { Marshal.FreeHGlobal(items); }
        }
    }

    const int IdleGpuClass = 0; // D3DKMT_SCHEDULINGPRIORITYCLASS_IDLE
    const int IdleThreadPriority = -15; // THREAD_PRIORITY_IDLE
    const int PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = unchecked((int)0x800007D2);

    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, int format, ref int size, out int count, IntPtr items);
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
readonly partial struct Burn(ReadWriteBuffer<float> buffer) : IComputeShader
{
    // Hotfix's one calculation, on the CPU and the GPU: x × Half + Tiny. It settles on Answer, 2⁻¹²⁵, and stays there,
    // so every multiply-add returns the number it was given. That is as close to nothing as a calculation of this
    // shape can settle while every value stays a normal float: anything smaller is subnormal, which CPUs handle slowly
    // and GPUs flush to zero, and either way they would make less heat. tests/AnswerCheck holds the code to it.
    public const float Half = 0.5f;
    public const float Tiny = 1.17549435E-38f; // 2⁻¹²⁶, the smallest normal float
    public const float Answer = 2.3509887E-38f; // 2⁻¹²⁵

    public void Execute()
    {
        // Eight independent chains, pure ALU: heat without taking VRAM bandwidth from your work. They start
        // apart so the compiler cannot fold them into one, and all settle on Answer within 150 steps.
        float a = buffer[ThreadIds.X], b = a + 1, c = a + 2, d = a + 3, e = a + 4, f = a + 5, g = a + 6, h = a + 7;
        for (int i = 0; i < 4096; i++)
        {
            a = a * Half + Tiny;
            b = b * Half + Tiny;
            c = c * Half + Tiny;
            d = d * Half + Tiny;
            e = e * Half + Tiny;
            f = f * Half + Tiny;
            g = g * Half + Tiny;
            h = h * Half + Tiny;
        }
        buffer[ThreadIds.X] = (a + b + c + d + e + f + g + h) * 0.125f; // their mean, which is Answer again
    }
}
