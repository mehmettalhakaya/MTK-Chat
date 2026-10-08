using System.Text;
using System.Text.Json;

namespace MTKChat.Desktop;

internal static class RuntimeDiagnosticsQA
{
    internal static IReadOnlyList<string> Verify(string directory)
    {
        var checks = new List<string>();
        void Require(bool condition, string check)
        {
            if (!condition) throw new InvalidOperationException("Runtime diagnostics regression: " + check);
            checks.Add(check);
        }
        var sentinel = "QA_SECRET_MESSAGE_SENTINEL";
        Exception error;
        try { ThrowProbe(sentinel); throw new InvalidOperationException("Probe did not throw."); }
        catch (InvalidOperationException exception) { error = exception; }
        error.Data["request"] = sentinel;
        var encoded = RuntimeDiagnostics.CreateEntry(error, RuntimeDiagnostics.FailureKind.UnhandledTermination);
        var text = Encoding.UTF8.GetString(encoded);
        Require(!text.Contains(sentinel, StringComparison.Ordinal), "Exception messages and Data are never serialized");
        using (var parsed = JsonDocument.Parse(text))
        {
            var first = parsed.RootElement.GetProperty("failures")[0];
            Require(first.GetProperty("type").GetString() == typeof(InvalidOperationException).FullName,
                "Exception type is retained without its message");
            Require(first.GetProperty("methods").EnumerateArray().Any(method => method.GetString()!.EndsWith(".ThrowProbe")),
                "Method names preserve the causal stack without source paths");
            Require(!text.Contains("C:\\", StringComparison.Ordinal) && !text.Contains("request", StringComparison.Ordinal),
                "No source directory or arbitrary exception data enters the report");
        }
        var probeDirectory = Path.Combine(directory, "runtime-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        for (var i = 0; i < 550; i++)
            RuntimeDiagnostics.Record(error, RuntimeDiagnostics.FailureKind.UnobservedBackgroundTask, probeDirectory);
        var logs = Directory.GetFiles(probeDirectory, "runtime-*.jsonl");
        Require(logs.Length <= RuntimeDiagnostics.MaximumLogFiles && logs.Length > 1,
            "Repeated reports rotate into a bounded set of log files");
        Require(logs.All(path => new FileInfo(path).Length <= RuntimeDiagnostics.MaximumLogBytes),
            "Every retained diagnostic file stays within its size bound");
        Require(logs.SelectMany(File.ReadLines).All(line => !line.Contains(sentinel, StringComparison.Ordinal)),
            "Written and rotated reports never contain the secret-like sentinel");
        var blocked = Path.Combine(probeDirectory, "not-a-directory");
        File.WriteAllText(blocked, "qa");
        RuntimeDiagnostics.Record(error, RuntimeDiagnostics.FailureKind.UnhandledTermination, blocked);
        Require(File.ReadAllText(blocked) == "qa", "An unwritable diagnostic location does not overwrite files or throw");
        return checks;
    }

    private static void ThrowProbe(string sentinel) => throw new InvalidOperationException(sentinel,
        new IOException(sentinel));
}
