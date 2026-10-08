using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MTKChat.Desktop;

// Crash reports deliberately exclude Exception.Message/ToString/Data, request
// URLs, user names, chat content and environment variables. A failure's type,
// HResult and method names are enough to identify the failing code path.
internal static class RuntimeDiagnostics
{
    internal enum FailureKind { UnhandledTermination, UnobservedBackgroundTask }
    internal const int MaximumLogBytes = 64 * 1024;
    internal const int MaximumLogFiles = 4;
    private static readonly object WriteGate = new();
    private static int _installed;
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTKChat", "Diagnostics");

    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        // Observe existing runtime policies; do not catch/swallow an unknown UI
        // error or pretend the process is safe to continue after corruption.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                Record(error, FailureKind.UnhandledTermination, LogDirectory);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Record(args.Exception, FailureKind.UnobservedBackgroundTask, LogDirectory);
    }

    internal static void Record(Exception error, FailureKind kind, string directory)
    {
        try
        {
            var entry = CreateEntry(error, kind);
            lock (WriteGate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "runtime-0.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + entry.Length > MaximumLogBytes)
                {
                    // Only the four exact diagnostic filenames are rotated.
                    // No user files, chat storage or directory trees are touched.
                    var oldest = Path.Combine(directory, $"runtime-{MaximumLogFiles - 1}.jsonl");
                    if (File.Exists(oldest)) File.Delete(oldest);
                    for (var i = MaximumLogFiles - 2; i >= 0; i--)
                    {
                        var prior = Path.Combine(directory, $"runtime-{i}.jsonl");
                        if (File.Exists(prior)) File.Move(prior, Path.Combine(directory, $"runtime-{i + 1}.jsonl"));
                    }
                }
                using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                output.Write(entry);
            }
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            // A locked/unwritable diagnostic folder must not introduce a second
            // process-killing failure while the original exception is reported.
        }
    }

    internal static byte[] CreateEntry(Exception error, FailureKind kind)
    {
        var failures = new List<object>();
        for (var current = error; current is not null && failures.Count < 3; current = current.InnerException)
        {
            var methods = new StackTrace(current, false).GetFrames()?.Take(32).Select(frame =>
            {
                var method = frame.GetMethod();
                return $"{method?.DeclaringType?.FullName}.{method?.Name}";
            }).ToArray() ?? [];
            failures.Add(new { type = current.GetType().FullName, hresult = current.HResult, methods });
        }
        var json = JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow,
            kind = kind.ToString(),
            processId = Environment.ProcessId,
            appVersion = typeof(RuntimeDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(RuntimeDiagnostics).Assembly.GetName().Version?.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            failures
        });
        return Encoding.UTF8.GetBytes(json + "\n");
    }
}
