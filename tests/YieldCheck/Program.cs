// Measures what Hotfix costs the work you care about. Runs a single-thread job, an all-core job and a GPU job
// at normal priority, first with Hotfix off and then with it running, and prints the slowdown and the power.
// Exits 1 if any job slows by more than the limit. Hotfix warms for a minute first: a chip that has been hot
// for a while has spent its turbo budget and clocks lower, so a short warm-up would flatter it. Usage:
//   dotnet run --project tests/YieldCheck -c Release -- path\to\Hotfix.exe [limit-percent] [warm-up-seconds]
using System.Diagnostics;
using System.Globalization;
using ComputeSharp;
using Microsoft.Win32;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: YieldCheck <path to Hotfix.exe> [limit-percent, default 10] [warm-up-seconds, default 60]");
    return 2;
}
double limit = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 10;
int warmUp = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 60;
if (Process.GetProcessesByName("Hotfix").Length > 0)
{
    Console.Error.WriteLine("Exit the running Hotfix first; the check starts its own.");
    return 2;
}
using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\Den Frie Vilje\Hotfix"))
    Console.WriteLine($"Hotfix settings: CPU {Setting(settings, "Cpu")}, GPU {Setting(settings, "Gpu")}");

var device = GraphicsDevice.GetDefault();
using var buffer = device.AllocateReadWriteBuffer<float>(1 << 21);
Console.WriteLine($"{Environment.ProcessorCount} logical processors; GPU {device.Name}");
Measure(); // warm up the JIT, the thread pool and the shader

var (idleCpuW, idleGpuW) = (CpuWatts(), GpuWatts());
var off = Measure();
using (var hotfix = Process.Start(args[0])!)
{
    Console.WriteLine($"Hotfix running; warming up for {warmUp} s");
    Thread.Sleep(warmUp * 1000);
    var (warmCpuW, warmGpuW) = (CpuWatts(), GpuWatts());
    var on = Measure();
    hotfix.Kill();
    hotfix.WaitForExit();

    Console.WriteLine();
    Console.WriteLine($"{"",-22}{"Hotfix off",12}{"Hotfix on",12}{"Change",10}");
    Console.WriteLine($"{"CPU package power",-22}{W(idleCpuW),12}{W(warmCpuW),12}");
    Console.WriteLine($"{"GPU board power",-22}{W(idleGpuW),12}{W(warmGpuW),12}");
    bool pass = true;
    foreach (var (name, a, b) in new[] { ("Single-thread job", off.Single, on.Single), ("All-core job", off.Multi, on.Multi), ("GPU job", off.Gpu, on.Gpu) })
    {
        double change = (b / a - 1) * 100;
        pass &= change <= limit;
        Console.WriteLine($"{name,-22}{a,10:F0}ms{b,10:F0}ms{change,9:+0.0;-0.0}%");
    }
    Console.WriteLine();
    Console.WriteLine(pass ? $"PASS: no job slowed by more than {limit}%." : $"FAIL: a job slowed by more than {limit}%.");
    return pass ? 0 : 1;
}

(double Single, double Multi, double Gpu) Measure()
{
    // Each job first runs untimed for two seconds, as real work has been going for a moment by the time its speed
    // matters, and Hotfix takes up to half a second to notice it. Then the median of five timed runs, in ms.
    double Steady(Action job)
    {
        var lead = Stopwatch.StartNew();
        while (lead.Elapsed.TotalSeconds < 2) job();
        return Enumerable.Range(0, 5).Select(_ => Time(job)).Order().ElementAt(2);
    }
    return (
        Steady(() => Spin()),
        Steady(() => Parallel.For(0, Environment.ProcessorCount, _ => Spin())),
        Steady(() => { for (int i = 0; i < 40; i++) device.For(buffer.Length, new Work(buffer)); }));
}

static double Time(Action job)
{
    var clock = Stopwatch.StartNew();
    job();
    return clock.Elapsed.TotalMilliseconds;
}

static ulong Spin()
{
    // A dependent chain of integer multiply-adds: pure core speed, no memory.
    ulong x = 1;
    for (int i = 0; i < 100_000_000; i++) x = x * 6364136223846793005UL + 1442695040888963407UL;
    return x;
}

static string Setting(RegistryKey? key, string name) => key?.GetValue(name) is int v ? (v != 0 ? "on" : "off") : "default";

static string W(double? watts) => watts is { } w ? $"{w:F0} W" : "n/a";

// RAPL package power through Windows' Energy Meter counter, in milliwatts; three one-second samples.
static double? CpuWatts() => Average(Run("typeperf", @"""\Energy Meter(RAPL_Package0_PKG)\Power"" -sc 3")
    .Where(l => l.StartsWith("\"") && !l.StartsWith("\"(PDH"))
    .Select(l => l.Split(',')[1].Trim('"')), 1000);

// NVIDIA only; other GPUs report n/a.
static double? GpuWatts() => Average(Enumerable.Range(0, 3)
    .SelectMany(_ => { Thread.Sleep(1000); return Run("nvidia-smi", "--query-gpu=power.draw --format=csv,noheader,nounits").Take(1); }), 1);

static double? Average(IEnumerable<string> values, double divisor)
{
    var parsed = values.Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
        .Where(d => !double.IsNaN(d)).ToList();
    return parsed.Count > 0 ? parsed.Average() / divisor : null;
}

static string[] Run(string exe, string arguments)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
    catch (System.ComponentModel.Win32Exception) { return []; } // tool not installed
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
readonly partial struct Work(ReadWriteBuffer<float> buffer) : IComputeShader
{
    public void Execute()
    {
        float x = buffer[ThreadIds.X];
        for (int i = 0; i < 16384; i++)
            x = x * 0.9999999f + 0.0000001f;
        buffer[ThreadIds.X] = x;
    }
}
