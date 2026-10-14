// Holds Hotfix to what the product page says it computes: x × ½ + 2⁻¹²⁶, settling on 2⁻¹²⁵ exactly, every value a
// normal float. Runs the shipped constants and the shipped shader, and exits 1 if any of it is not so. Usage:
//   dotnet run --project tests/AnswerCheck -c Release
using System.Diagnostics;
using System.Runtime.Intrinsics;
using ComputeSharp;
using Hotfix;

bool ok = true;
void Check(bool pass, string what)
{
    Console.WriteLine($"{(pass ? "ok  " : "FAIL")} {what}");
    ok &= pass;
}

Check(Burn.Tiny == MathF.ScaleB(1, -126) && float.IsNormal(Burn.Tiny) && !float.IsNormal(Burn.Tiny / 2),
    "the constant is 2^-126, the smallest normal float");
Check(Burn.Answer == MathF.ScaleB(1, -125), "the answer is 2^-125");
var x = Vector256.Create(Burn.Answer);
Check(Vector256.FusedMultiplyAdd(x, Vector256.Create(Burn.Half), Vector256.Create(Burn.Tiny)) == x,
    "on the CPU, a multiply-add returns the number it was given");
Check(float.IsNormal(Burn.Answer * Burn.Half), "nothing on the way there is subnormal");

GraphicsDevice? device = null;
try { device = GraphicsDevice.GetDefault(); } catch (Exception) { }
if (device is null)
{
    Console.WriteLine("skip no DirectX 12 device for the GPU half");
    return ok ? 0 : 1;
}

// The shipped buffer and shader. A software device runs only a few thousand threads, to stay quick.
using var buffer = Heater.NewBuffer(device);
int threads = device.IsHardwareAccelerated ? buffer.Length : 4096;
for (int pass = 0; pass < 3; pass++)
    device.For(threads, new Burn(buffer));
var result = buffer.ToArray();
Check(result.All(v => v == Burn.Answer), $"on {device.Name}, all {threads:N0} threads return 2^-125 after three passes");

if (device.IsHardwareAccelerated)
{
    // Eight chains of 4,096 multiply-adds per thread, per pass.
    const int passes = 50;
    var clock = Stopwatch.StartNew();
    for (int pass = 0; pass < passes; pass++)
        device.For(buffer.Length, new Burn(buffer));
    double perSecond = (double)passes * buffer.Length * 8 * 4096 / clock.Elapsed.TotalSeconds;
    Console.WriteLine($"     {perSecond / 1e12:F1} trillion multiply-adds a second on {device.Name}, each returning 2^-125");
}
return ok ? 0 : 1;
