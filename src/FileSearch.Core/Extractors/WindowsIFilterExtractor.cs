using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FileSearch.Core.Extractors;

public sealed class WindowsIFilterExtractor : IDiagnosticTextExtractor
{
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int FilterEEndOfChunks = unchecked((int)0x80041700);
    private const int FilterENoMoreText = unchecked((int)0x80041701);
    private const int FilterENoMoreValues = unchecked((int)0x80041702);
    private const int FilterEAccess = unchecked((int)0x80041703);
    private const int FilterEEmbeddingUnavailable = unchecked((int)0x80041705);
    private const int FilterELinkUnavailable = unchecked((int)0x80041706);
    private const int FilterSLastText = 0x00041709;
    private const int FilterEPassword = unchecked((int)0x8004170B);
    private const int FilterEUnknownFormat = unchecked((int)0x8004170C);
    private const int TextBufferLength = 8192;

    private static readonly Guid s_iFilterInterfaceId = new("89BCB740-6119-101A-BCB7-00DD010655AF");

    private readonly WindowsIFilterExtractionOptions _options;

    public WindowsIFilterExtractor()
        : this(null)
    {
    }

    public WindowsIFilterExtractor(WindowsIFilterExtractionOptions? options)
    {
        _options = options ?? new WindowsIFilterExtractionOptions();
    }

    public string ExtractorId => "filesearch.ifilter";

    public string ExtractorVersion => "2";

    public IReadOnlyCollection<string> SupportedExtensions { get; } = Array.Empty<string>();

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in ExtractAsync(path, NullExtractionIssueSink.Instance, cancellationToken).ConfigureAwait(false))
            yield return line;
    }

    public async IAsyncEnumerable<TextLine> ExtractAsync(
        string path,
        IExtractionIssueSink issues,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var lines = ExtractSync(path, issues, cancellationToken);
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    private List<TextLine> ExtractSync(
        string path,
        IExtractionIssueSink issues,
        CancellationToken cancellationToken)
    {
        var lines = new List<TextLine>();
        if (!OperatingSystem.IsWindows())
        {
            ReportIssue(issues, "ifilter_unsupported_platform", "Windows IFilter extraction is only available on Windows.");
            return lines;
        }

        if (!_options.Enabled)
        {
            ReportIssue(issues, "ifilter_disabled", "Windows IFilter fallback is disabled.");
            return lines;
        }

        if (!_options.AllowsPath(path))
        {
            ReportIssue(issues, "ifilter_blocked", "Windows IFilter fallback is disabled for this file type.");
            return lines;
        }

        if (!File.Exists(path))
        {
            ReportIssue(issues, "ifilter_file_missing", "File does not exist.");
            return lines;
        }

        if (!HasIFilterRegistration(path))
        {
            ReportIssue(issues, "ifilter_not_registered", "No Windows IFilter is registered for this file type.");
            return lines;
        }

        var comInitialized = false;
        var initHr = CoInitializeEx(IntPtr.Zero, CoInitMultiThreaded);
        if (initHr == SOk || initHr == SFalse)
        {
            comInitialized = true;
        }
        else if (initHr != RpcEChangedMode)
        {
            ReportIssue(issues, "ifilter_com_init_failed", $"COM initialization failed: {FormatHResult(initHr)}.");
            return lines;
        }

        IFilter? filter = null;
        try
        {
            int hr;
            try
            {
                hr = LoadIFilter(path, IntPtr.Zero, out filter);
            }
            catch (DllNotFoundException ex)
            {
                ReportIssue(issues, "ifilter_load_failed", $"Windows IFilter loader is not available: {ex.Message}");
                return lines;
            }
            catch (EntryPointNotFoundException ex)
            {
                ReportIssue(issues, "ifilter_load_failed", $"Windows IFilter loader entry point is not available: {ex.Message}");
                return lines;
            }

            if (hr != SOk || filter is null)
            {
                ReportIssue(issues, MapLoadFailureCode(hr), $"Windows IFilter could not be loaded: {FormatHResult(hr)}.");
                return lines;
            }

            hr = filter.Init(
                IFilterInit.CanonParagraphs |
                IFilterInit.HardLineBreaks |
                IFilterInit.CanonHyphens |
                IFilterInit.CanonSpaces |
                IFilterInit.ApplyIndexAttributes |
                IFilterInit.ApplyOtherAttributes |
                IFilterInit.IndexingOnly,
                0,
                IntPtr.Zero,
                out _);
            if (hr < 0)
            {
                ReportIssue(issues, "ifilter_init_failed", $"Windows IFilter initialization failed: {FormatHResult(hr)}.");
                return lines;
            }

            var content = new List<string>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hr = filter.GetChunk(out var chunk);
                if (hr == FilterEEndOfChunks)
                    break;

                if (hr < 0)
                {
                    if (TryHandleRecoverableChunkFailure(issues, hr))
                        continue;

                    ReportIssue(issues, MapChunkFailureCode(hr), $"Windows IFilter chunk extraction failed: {FormatHResult(hr)}.");
                    break;
                }

                if ((chunk.Flags & ChunkState.Text) != 0)
                    ReadChunkText(filter, issues, content, cancellationToken);

                if ((chunk.Flags & ChunkState.Value) != 0)
                    ReadChunkValue(filter, chunk.Attribute, issues, content, cancellationToken);
            }

            AddTextLines(content, lines);
            if (lines.Count == 0)
                ReportIssue(issues, "ifilter_empty", "Windows IFilter completed but returned no text.");
            return lines;
        }
        finally
        {
            if (filter is not null)
                Marshal.ReleaseComObject(filter);

            if (comInitialized)
                CoUninitialize();
        }
    }

    private static void ReadChunkValue(
        IFilter filter,
        FullPropSpec attribute,
        IExtractionIssueSink issues,
        List<string> content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hr = filter.GetValue(out var propValue);
        if (hr == FilterENoMoreValues)
            return;

        if (hr < 0)
        {
            ReportIssue(issues, "ifilter_value_failed", $"Windows IFilter property extraction failed: {FormatHResult(hr)}.");
            return;
        }

        if (propValue == IntPtr.Zero)
            return;

        try
        {
            var value = TryReadPropVariant(propValue);
            if (string.IsNullOrWhiteSpace(value))
                return;

            content.Add(Environment.NewLine);
            content.Add($"{DescribeProperty(attribute)}: {value}");
            content.Add(Environment.NewLine);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or OverflowException)
        {
            ReportIssue(issues, "ifilter_value_unsupported", $"Windows IFilter returned an unsupported property value: {ex.Message}");
        }
        finally
        {
            _ = PropVariantClear(propValue);
            Marshal.FreeCoTaskMem(propValue);
        }
    }

    private static void ReadChunkText(
        IFilter filter,
        IExtractionIssueSink issues,
        List<string> content,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = TextBufferLength;
            var buffer = new char[TextBufferLength];
            var hr = filter.GetText(ref count, buffer);
            if (hr == FilterENoMoreText)
                break;

            if (hr == SOk || hr == FilterSLastText)
            {
                if (count > 0)
                    content.Add(new string(buffer, 0, count));

                if (hr == FilterSLastText)
                    break;

                continue;
            }

            if (hr < 0)
            {
                ReportIssue(issues, MapTextFailureCode(hr), $"Windows IFilter text extraction failed: {FormatHResult(hr)}.");
                break;
            }
        }
    }

    private static string? TryReadPropVariant(IntPtr propVariant)
    {
        var type = (VariantType)Marshal.ReadInt16(propVariant);
        const int dataOffset = 8;
        return type switch
        {
            VariantType.Empty or VariantType.Null => null,
            VariantType.I1 => unchecked((sbyte)Marshal.ReadByte(propVariant, dataOffset)).ToString(CultureInfo.InvariantCulture),
            VariantType.UI1 => Marshal.ReadByte(propVariant, dataOffset).ToString(CultureInfo.InvariantCulture),
            VariantType.I2 => Marshal.ReadInt16(propVariant, dataOffset).ToString(CultureInfo.InvariantCulture),
            VariantType.UI2 => unchecked((ushort)Marshal.ReadInt16(propVariant, dataOffset)).ToString(CultureInfo.InvariantCulture),
            VariantType.I4 or VariantType.Int => Marshal.ReadInt32(propVariant, dataOffset).ToString(CultureInfo.InvariantCulture),
            VariantType.UI4 or VariantType.UInt => unchecked((uint)Marshal.ReadInt32(propVariant, dataOffset)).ToString(CultureInfo.InvariantCulture),
            VariantType.I8 => Marshal.ReadInt64(propVariant, dataOffset).ToString(CultureInfo.InvariantCulture),
            VariantType.UI8 => unchecked((ulong)Marshal.ReadInt64(propVariant, dataOffset)).ToString(CultureInfo.InvariantCulture),
            VariantType.R4 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(propVariant, dataOffset)).ToString(CultureInfo.InvariantCulture),
            VariantType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(propVariant, dataOffset)).ToString(CultureInfo.InvariantCulture),
            VariantType.Bool => Marshal.ReadInt16(propVariant, dataOffset) != 0 ? "true" : "false",
            VariantType.LPStr => Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(propVariant, dataOffset)),
            VariantType.LPWStr => Marshal.PtrToStringUni(Marshal.ReadIntPtr(propVariant, dataOffset)),
            VariantType.BStr => Marshal.PtrToStringBSTR(Marshal.ReadIntPtr(propVariant, dataOffset)),
            VariantType.FileTime => ReadFileTime(propVariant, dataOffset),
            _ => null,
        };
    }

    private static string? ReadFileTime(IntPtr propVariant, int dataOffset)
    {
        var low = unchecked((uint)Marshal.ReadInt32(propVariant, dataOffset));
        var high = unchecked((uint)Marshal.ReadInt32(propVariant, dataOffset + 4));
        var fileTime = unchecked((long)(((ulong)high << 32) | low));
        return fileTime <= 0
            ? null
            : DateTime.FromFileTimeUtc(fileTime).ToString("O", CultureInfo.InvariantCulture);
    }

    private static string DescribeProperty(FullPropSpec attribute)
    {
        const int prSpecLpwstr = 0;
        const int prSpecPropId = 1;
        if (attribute.PropSpec.Kind == prSpecLpwstr && attribute.PropSpec.Value != IntPtr.Zero)
        {
            var name = Marshal.PtrToStringUni(attribute.PropSpec.Value);
            if (!string.IsNullOrWhiteSpace(name))
                return $"ifilter property {name}";
        }

        if (attribute.PropSpec.Kind == prSpecPropId)
            return FormattableString.Invariant($"ifilter property {attribute.GuidPropSet:B}:{attribute.PropSpec.Value.ToInt64()}");

        return FormattableString.Invariant($"ifilter property {attribute.GuidPropSet:B}");
    }

    private static void AddTextLines(IReadOnlyCollection<string> chunks, List<TextLine> lines)
    {
        var combined = string.Concat(chunks);
        if (string.IsNullOrWhiteSpace(combined))
            return;

        var lineNumber = 0;
        using var reader = new StringReader(combined);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            lineNumber++;
            lines.Add(new TextLine(lineNumber, line.TrimEnd('\r')));
        }
    }

    private static bool TryHandleRecoverableChunkFailure(IExtractionIssueSink issues, int hr)
    {
        switch (hr)
        {
            case FilterEEmbeddingUnavailable:
                ReportIssue(issues, "ifilter_embedding_unavailable", "Embedded content was skipped because no IFilter was available.");
                return true;
            case FilterELinkUnavailable:
                ReportIssue(issues, "ifilter_link_unavailable", "Linked content was skipped because no IFilter was available.");
                return true;
            default:
                return false;
        }
    }

    private static string MapLoadFailureCode(int hr) =>
        hr switch
        {
            EAccessDenied => "ifilter_access_denied",
            FilterEPassword => "ifilter_password",
            FilterEUnknownFormat => "ifilter_unknown_format",
            _ => "ifilter_load_failed",
        };

    private static string MapChunkFailureCode(int hr) =>
        hr switch
        {
            FilterEAccess => "ifilter_access_denied",
            FilterEPassword => "ifilter_password",
            FilterEUnknownFormat => "ifilter_unknown_format",
            _ => "ifilter_chunk_failed",
        };

    private static string MapTextFailureCode(int hr) =>
        hr switch
        {
            FilterEAccess => "ifilter_access_denied",
            FilterEPassword => "ifilter_password",
            _ => "ifilter_text_failed",
        };

    private static void ReportIssue(IExtractionIssueSink issues, string code, string message)
    {
        issues.Report(new ExtractionIssue(MemberPath: null, code, message));
    }

    private static string FormatHResult(int hr) => $"0x{hr:X8}";

    [SupportedOSPlatform("windows")]
    private static bool HasIFilterRegistration(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        var persistentHandler = ReadDefaultValue($@"{extension}\PersistentHandler");
        if (string.IsNullOrWhiteSpace(persistentHandler))
        {
            var progId = ReadDefaultValue(extension);
            if (!string.IsNullOrWhiteSpace(progId))
            {
                persistentHandler = ReadDefaultValue($@"{progId}\PersistentHandler");
                if (string.IsNullOrWhiteSpace(persistentHandler))
                {
                    var classId = ReadDefaultValue($@"{progId}\CLSID");
                    if (!string.IsNullOrWhiteSpace(classId))
                        persistentHandler = ReadDefaultValue($@"CLSID\{classId}\PersistentHandler");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(persistentHandler))
            return false;

        var filterAddin = ReadDefaultValue(
            $@"CLSID\{persistentHandler}\PersistentAddinsRegistered\{s_iFilterInterfaceId:B}");
        return !string.IsNullOrWhiteSpace(filterAddin);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadDefaultValue(string subKey)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(subKey);
        return key?.GetValue(null) as string;
    }

    private const uint CoInitMultiThreaded = 0x0;

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr pvar);

    [DllImport("query.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LoadIFilter(
        [MarshalAs(UnmanagedType.LPWStr)] string pwcsPath,
        IntPtr pUnkOuter,
        [MarshalAs(UnmanagedType.Interface)] out IFilter ppIUnk);

    [ComImport]
    [Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig]
        int Init(
            IFilterInit grfFlags,
            int cAttributes,
            IntPtr aAttributes,
            out int pdwFlags);

        [PreserveSig]
        int GetChunk(out StatChunk pStat);

        [PreserveSig]
        int GetText(
            ref int pcwcBuffer,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] char[] awcBuffer);

        [PreserveSig]
        int GetValue(out IntPtr ppPropValue);

        [PreserveSig]
        int BindRegion(
            ref FilterRegion origPos,
            ref Guid riid,
            out IntPtr ppunk);
    }

    [Flags]
    private enum IFilterInit
    {
        CanonParagraphs = 1,
        HardLineBreaks = 2,
        CanonHyphens = 4,
        CanonSpaces = 8,
        ApplyIndexAttributes = 16,
        ApplyOtherAttributes = 32,
        IndexingOnly = 64,
    }

    [Flags]
    private enum ChunkState
    {
        Text = 1,
        Value = 2,
        FilterOwnedValue = 4,
    }

    private enum ChunkBreakType
    {
        NoBreak = 0,
        EndOfWord = 1,
        EndOfSentence = 2,
        EndOfParagraph = 3,
        EndOfChapter = 4,
    }

    private enum VariantType : ushort
    {
        Empty = 0,
        Null = 1,
        I2 = 2,
        I4 = 3,
        R4 = 4,
        R8 = 5,
        Bool = 11,
        I1 = 16,
        UI1 = 17,
        UI2 = 18,
        UI4 = 19,
        I8 = 20,
        UI8 = 21,
        Int = 22,
        UInt = 23,
        BStr = 8,
        LPStr = 30,
        LPWStr = 31,
        FileTime = 64,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatChunk
    {
        public int IdChunk;
        public ChunkBreakType BreakType;
        public ChunkState Flags;
        public int Locale;
        public FullPropSpec Attribute;
        public int IdChunkSource;
        public int CwcStartSource;
        public int CwcLenSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FullPropSpec
    {
        public Guid GuidPropSet;
        public PropSpec PropSpec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropSpec
    {
        public int Kind;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterRegion
    {
        public int IdChunk;
        public int CwcStart;
        public int CwcExtent;
    }
}
