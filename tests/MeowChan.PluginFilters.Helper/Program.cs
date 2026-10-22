using System.Diagnostics;
using System.Security.Cryptography;
if (args.Length == 1 && args[0] == "child") { await Task.Delay(Timeout.Infinite); return; }
if (args[1] == "hang")
{
    using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, ArgumentList = { "child" } })!;
    await File.WriteAllTextAsync("helper-pids.next", $"{Environment.ProcessId},{child.Id}");
    File.Move("helper-pids.next", "helper-pids");
    await Task.Delay(Timeout.Infinite); return;
}
if (args[1] == "temp") { await File.WriteAllBytesAsync("helper-copy.bin", new byte[65536]); await Task.Delay(Timeout.Infinite); return; }
await using FileStream input = File.OpenRead(args[0]);
Console.WriteLine(Convert.ToHexString(await SHA256.HashDataAsync(input)));
