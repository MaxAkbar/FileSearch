using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Forms = System.Windows.Forms;

namespace FileSearch.Gui.Services;

/// <summary>Runs installed preview handlers in an expendable GUI process, before normal app startup.</summary>
internal static class NativePreviewHandler
{
    private const string PreviewInterface = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    public static Guid? Find(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var output = new char[128];
        uint length = (uint)output.Length;
        return AssocQueryString(0, 16, extension, PreviewInterface, output, ref length) == 0 &&
            Guid.TryParse(new string(output).TrimEnd('\0'), out var id) ? id : null;
    }

    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--file-preview-host") return false;
        if (args.Length != 4 || !long.TryParse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle) ||
            !args[2].StartsWith("FileSearchPreview-", StringComparison.Ordinal) || !File.Exists(args[3])) return true;
        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.Out);
        try
        {
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var parent = new IntPtr(handle);
            var id = Find(Path.GetExtension(args[3]));
            if (id is null || !IsWindow(parent)) { writer.WriteLine("unavailable"); return true; }
            using var form = new Forms.Form { TopLevel = false, FormBorderStyle = Forms.FormBorderStyle.None, ShowInTaskbar = false };
            SetParent(form.Handle, parent);
            object? instance = null;
            IStream? stream = null;
            object? shellItem = null;
            IPreviewHandler? preview = null;
            using var timer = new Forms.Timer { Interval = 200 };
            Rect last = default;
            void Resize()
            {
                if (!IsWindow(parent)) { form.Close(); return; }
                GetClientRect(parent, out var rect);
                if (rect.Right == last.Right && rect.Bottom == last.Bottom) return;
                last = rect;
                form.SetBounds(0, 0, rect.Right, rect.Bottom);
                preview?.SetRect(ref rect);
            }
            form.Shown += (_, _) => form.BeginInvoke(() =>
            {
                try
                {
                    var classId = id.Value;
                    var interfaceId = typeof(IPreviewHandler).GUID;
                    // Ask COM for the registered out-of-process preview server/surrogate.
                    var activation = CoCreateInstance(ref classId, IntPtr.Zero, 4, ref interfaceId, out instance);
                    // Some installed handlers expose only a DLL. Load those into this dedicated helper, never the main app.
                    if (activation == unchecked((int)0x80040154))
                        activation = CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out instance);
                    Marshal.ThrowExceptionForHR(activation);
                    preview = (IPreviewHandler)instance!;
                    if (instance is IInitializeWithStream fromStream)
                    {
                        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(args[3], 0x40, 0, false, IntPtr.Zero, out stream));
                        fromStream.Initialize(stream, 0);
                    }
                    else if (instance is IInitializeWithFile fromFile) fromFile.Initialize(args[3], 0);
                    else if (instance is IInitializeWithItem fromItem)
                    {
                        var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(args[3], IntPtr.Zero, ref iid, out shellItem));
                        fromItem.Initialize(shellItem, 0);
                    }
                    else throw new NotSupportedException();
                    if (instance is IObjectWithSite site) site.SetSite(new PreviewSite(form.Handle));
                    Resize();
                    GetClientRect(form.Handle, out var rect);
                    preview.SetWindow(form.Handle, ref rect);
                    preview.DoPreview();
                    writer.WriteLine("ready");
                    timer.Start();
                }
                catch (Exception)
                {
                    try { writer.WriteLine("unavailable"); } catch (IOException) { }
                    form.Close();
                }
            });
            timer.Tick += (_, _) => Resize();
            form.FormClosed += (_, _) => Forms.Application.ExitThread();
            Resize();
            form.Show();
            Forms.Application.Run();
            try { preview?.Unload(); } catch (COMException) { }
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
            if (stream is not null) Marshal.FinalReleaseComObject(stream);
            if (shellItem is not null && Marshal.IsComObject(shellItem)) Marshal.FinalReleaseComObject(shellItem);
        }
        catch (Exception) { /* An unavailable handler never starts the normal application. */ }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref Rect rect);
        void SetRect(ref Rect rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
    }
    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }
    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }
    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem { void Initialize([MarshalAs(UnmanagedType.IUnknown)] object item, uint mode); }
    [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite { void SetSite([MarshalAs(UnmanagedType.IUnknown)] object site); void GetSite(ref Guid iid, out IntPtr site); }
    [StructLayout(LayoutKind.Sequential)]
    public struct FrameInfo { public IntPtr Accelerator; public uint Count; }
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [ComVisible(true), Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandlerFrame
    {
        void GetWindowContext(out FrameInfo info);
        [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
    }
    [ComVisible(true), Guid("00000114-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleWindow
    {
        void GetWindow(out IntPtr window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class PreviewSite(IntPtr window) : IPreviewHandlerFrame, IOleWindow
    {
        public void GetWindowContext(out FrameInfo info) => info = default;
        public int TranslateAccelerator(ref NativeMessage message) => 1;
        public void GetWindow(out IntPtr handle) => handle = window;
        public void ContextSensitiveHelp(bool enterMode) { }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    private static extern int AssocQueryString(uint flags, uint kind, string association, string extra, [Out] char[] output, ref uint length);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IStream stream);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object instance);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
}

/// <summary>Owns one cancellable handler process and its private, current-user-only readiness pipe.</summary>
internal sealed class NativePreviewSession : IDisposable
{
    private Process? _process;
    public async Task<bool> StartAsync(IntPtr parent, string path, CancellationToken token)
    {
        var name = "FileSearchPreview-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FileSearch.Gui.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "--file-preview-host", parent.ToInt64().ToString("x", CultureInfo.InvariantCulture), name, Path.GetFullPath(path) })
            start.ArgumentList.Add(value);
        _process = Process.Start(start);
        using var registration = token.Register(Dispose);
        if (_process is null) return false;
        var exited = _process.WaitForExitAsync(token);
        var connected = pipe.WaitForConnectionAsync(token);
        if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited) return false;
        await connected.ConfigureAwait(false);
        using var reader = new StreamReader(pipe);
        var response = reader.ReadLineAsync(token).AsTask();
        if (await Task.WhenAny(response, exited).ConfigureAwait(false) == exited) return false;
        return await response.ConfigureAwait(false) == "ready";
    }
    public Task WaitForExitAsync(CancellationToken token) => _process?.WaitForExitAsync(token) ?? Task.CompletedTask;
    public void Dispose()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
}
