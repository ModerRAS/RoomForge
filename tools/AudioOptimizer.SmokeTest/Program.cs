using AudioOptimizer.Audio;
using AudioOptimizer.SmokeTest;

// Device names are arbitrary UTF-16; force UTF-8 so a non-ASCII endpoint name is not mangled on either console.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Last-resort net: the harness runs against real hardware, so a failure must be one readable line and a
// non-zero exit code — never an unhandled exception and a stack trace.
try
{
    return SmokeTestRunner.Run(args, Console.Out);
}
catch (AudioDeviceOpenException failure)
{
    Console.Error.WriteLine($"error: {failure.Message}");
    Console.Error.WriteLine($"       (kind: {failure.Kind}, device: {failure.DeviceName})");
    return 1;
}
catch (Exception failure)
{
    Console.Error.WriteLine($"error: {failure.GetType().Name}: {failure.Message}");
    return 1;
}
