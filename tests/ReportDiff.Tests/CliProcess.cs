using System.Diagnostics;
using System.Text;
using ReportDiff.Cli;

namespace ReportDiff.Tests;

internal sealed record CliResult(int Code, string Output, string Error);

internal static class CliProcess
{
    public static Task<CliResult> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CliApplication.Run(args, output, error);
        return Task.FromResult(new CliResult(code, output.ToString(), error.ToString()));
    }

    public static async Task<CliResult> RunProcess(params string[] args)
    {
        var start = new ProcessStartInfo(ResolveDotnetPath())
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return new(process.ExitCode, await output, await error);
    }

    private static string ResolveDotnetPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
            return host;
        var candidate = Path.GetFullPath(Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(candidate) ? candidate : "dotnet";
    }
}
