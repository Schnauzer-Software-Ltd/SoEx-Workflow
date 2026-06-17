using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PiiMaker.Host.Restate;

/// <summary>
/// Builds, spawns and stops the example's Rust Restate sidecar, plus the small reachability/port helpers the
/// host startup needs. Infrastructure only — no governance or business logic lives here.
/// </summary>
internal static class RestateSidecar
{
    public static bool Reachable(string host, int port)
    {
        try { using var t = new TcpClient(); return t.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)) && t.Connected; }
        catch { return false; }   // refused/timeout (Wait wraps SocketException in AggregateException) → not reachable
    }

    public static bool WaitForPort(int port, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Reachable("127.0.0.1", port)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    public static void TryBuild(string dir)
    {
        string cargo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin", "cargo");
        if (!File.Exists(cargo)) cargo = "cargo";
        try
        {
            using Process? build = Process.Start(new ProcessStartInfo(cargo, "build --release")
            { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true });
            build?.WaitForExit((int)TimeSpan.FromMinutes(8).TotalMilliseconds);
        }
        catch { /* cargo unavailable — caller reports when the binary is still absent */ }
    }

    public static Process Start(string bin, string dir)
    {
        var psi = new ProcessStartInfo(bin) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["STEP_URL"] = "http://127.0.0.1:9091";
        psi.Environment["PORTABLE_STEP_URL"] = "http://127.0.0.1:9092";   // MembershipPortable -> the renewal /step host
        psi.Environment["STEP_TOKEN"] = "pii-example-token";
        psi.Environment["BIND"] = "127.0.0.1:9081";
        Process proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start the Restate sidecar");
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    public static void Kill(Process sidecar)
    {
        try { if (!sidecar.HasExited) { sidecar.Kill(entireProcessTree: true); sidecar.WaitForExit(3000); } } catch { /* best-effort */ }
        sidecar.Dispose();
    }

    public static StringContent JsonBody(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
