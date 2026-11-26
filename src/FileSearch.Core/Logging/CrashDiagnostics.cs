using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FileSearch.Core.Logging;

public enum CrashProcessRole
{
    Desktop,
    PdfRenderWorker,
    NativePreviewWorker,
    Indexer,
    ExtractorHost,
    Cli,
    McpServer,
    PowerToys,
}

public static class CrashDiagnostics
{
    public static string ReportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSearch", "CrashReports");

    // Called once at process startup after selecting the role, before starting its work.
    public static void Initialize(CrashProcessRole role)
    {
        var operation = role switch
        {
            CrashProcessRole.PdfRenderWorker => "RenderPage",
            CrashProcessRole.NativePreviewWorker => "ShowNativePreview",
            CrashProcessRole.Indexer => "RunIndexer",
            CrashProcessRole.ExtractorHost => "ExtractText",
            CrashProcessRole.Cli => "RunCommand",
            CrashProcessRole.McpServer => "ServeMcp",
            CrashProcessRole.PowerToys => "ServeCommandPalette",
            _ => "RunDesktop",
        };
        var build = typeof(CrashDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var host = Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.IsTerminating && args.ExceptionObject is Exception error)
                TryWriteReport(role, operation, build, host, error);
        };

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
        {
            // WER registration is advisory. A failing HRESULT must not stop the app.
            _ = WerRegisterCustomMetadata("FileSearchRole", role.ToString());
            _ = WerRegisterCustomMetadata("FileSearchOperation", operation);
            _ = WerRegisterCustomMetadata("FileSearchBuild", build);
        }
    }

    private static void TryWriteReport(CrashProcessRole role, string operation, string build, string host, Exception error)
    {
        try
        {
            var directory = Directory.CreateDirectory(ReportDirectory);
            var path = Path.Combine(directory.FullName, $"{role}.txt");
            var temporary = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, FormatReport(role, operation, build, host, error));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // Preserve the original unhandled exception and normal Windows crash reporting.
        }
    }

    internal static string FormatReport(CrashProcessRole role, string operation, string build, string host, Exception error)
    {
        var culture = CultureInfo.InvariantCulture;
        var report = new StringBuilder()
            .AppendLine(culture, $"UTC: {DateTime.UtcNow:O}")
            .AppendLine(culture, $"Build: {build}")
            .AppendLine(culture, $"Host: {host}")
            .AppendLine(culture, $"Role: {role}")
            .AppendLine(culture, $"Operation: {operation}")
            .AppendLine(culture, $"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})")
            .AppendLine(culture, $"OS: {Environment.OSVersion.Version}");

        // Exception messages, exception.Data, source paths and arguments can contain user content.
        var depth = 0;
        for (Exception? cause = error; cause is not null && depth++ < 4; cause = cause.InnerException)
        {
            report.AppendLine(culture, $"Exception: {cause.GetType().FullName} (0x{cause.HResult:X8})");
            var frameCount = 0;
            foreach (var frame in new StackTrace(cause, fNeedFileInfo: false).GetFrames())
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType;
                if (type?.Assembly.GetName().Name?.StartsWith("FileSearch.", StringComparison.Ordinal) == true)
                {
                    var declaringType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                    report.AppendLine(culture, $"  {declaringType.FullName}.{method!.Name}");
                    if (++frameCount == 12) break;
                }
            }
        }
        return report.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WerRegisterCustomMetadata(string key, string value);
}
