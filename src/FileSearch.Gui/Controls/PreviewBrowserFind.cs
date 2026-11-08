using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace FileSearch.Gui.Controls;

/// <summary>Owns a native browser Find session without changing document script permissions.</summary>
internal sealed class PreviewBrowserFind : IDisposable
{
    private readonly CoreWebView2Find _find;
    private readonly CoreWebView2FindOptions _options;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private int _generation, _activeGeneration = -1, _operations;
    private bool _disposed, _unavailable, _managedDisposed, _restoring;

    public PreviewBrowserFind(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        // Both interfaces are feature-detected here. Older runtimes can throw
        // NotImplementedException; the host keeps the ordinary preview available.
        _find = core.Find;
        _options = core.Environment.CreateFindOptions();
        _find.MatchCountChanged += OnNativeChanged;
        _find.ActiveMatchIndexChanged += OnNativeChanged;
    }

    public int MatchCount { get; private set; }
    public int ActiveMatchIndex { get; private set; } = -1;
    public bool IsSearching { get; private set; }
    public bool IsAvailable => !_disposed && !_unavailable;
    public event EventHandler? Changed;

    public async Task FindAsync(string query, bool matchCase, CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) { Stop(); return; }
        if (query.Length > 2048) throw new ArgumentException("Preview Find is limited to 2,048 characters.", nameof(query));
        if (_unavailable) throw new InvalidOperationException("Find is unavailable for this browser preview. Reopen the preview to retry.");

        int generation = ++_generation;
        _activeGeneration = -1;
        _request?.Cancel();
        StopNative();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(15));
        _request = request;
        _operations++;
        MatchCount = 0; ActiveMatchIndex = -1; IsSearching = true;
        bool entered = false;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
            await _gate.WaitAsync(request.Token);
            entered = true;
            request.Token.ThrowIfCancellationRequested();
            if (generation != _generation || _disposed) return;
            if (_unavailable) throw new InvalidOperationException("Find is unavailable for this browser preview. Reopen the preview to retry.");
            _find.Stop();
            _options.FindTerm = query;
            _options.IsCaseSensitive = matchCase;
            _options.ShouldMatchWord = false;
            _options.ShouldHighlightAllMatches = true;
            _options.SuppressDefaultFindDialog = true;
            Task native = _find.StartAsync(_options);
            try
            {
                await native.WaitAsync(request.Token);
            }
            catch (OperationCanceledException)
            {
                if (generation == _generation && !_disposed) StopNative();
                if (!native.IsCompleted)
                {
                    // A canceled managed wait does not cancel the native call.
                    // Keep its gate until completion so a later query cannot race it.
                    entered = false;
                    _operations++;
                    _ = ReleaseAfterNativeAsync(native);
                }
                throw;
            }
            request.Token.ThrowIfCancellationRequested();
            if (generation != _generation || _disposed) return;
            _activeGeneration = generation;
            IsSearching = false;
            PublishNativeState();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            bool timedOut = !token.IsCancellationRequested && !_lifetime.IsCancellationRequested && generation == _generation;
            if (generation == _generation && !_disposed)
            {
                StopNative();
                _unavailable |= timedOut;
                MatchCount = 0; ActiveMatchIndex = -1; IsSearching = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            if (timedOut) throw new TimeoutException("Browser Find took too long. Reopen the preview to retry.");
            throw;
        }
        catch
        {
            if (generation == _generation && !_disposed)
            {
                _unavailable = true; _activeGeneration = -1;
                MatchCount = 0; ActiveMatchIndex = -1; IsSearching = false;
                StopNative();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            throw;
        }
        finally
        {
            if (entered) _gate.Release();
            if (ReferenceEquals(_request, request)) _request = null;
            _operations--;
            DisposeManagedWhenIdle();
        }
    }

    public void Move(int direction)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_unavailable || IsSearching || _restoring || _activeGeneration != _generation || MatchCount == 0 || direction == 0) return;
        try
        {
            if (direction < 0) _find.FindPrevious(); else _find.FindNext();
            PublishNativeState();
        }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or COMException)
        {
            _unavailable = true;
            Stop();
            throw;
        }
    }

    internal async Task MoveToAsync(int matchNumber, CancellationToken token)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_unavailable || IsSearching || _restoring || _activeGeneration != _generation ||
            matchNumber < 1 || matchNumber > MatchCount) return;

        int generation = _generation;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(15));
        _request = request;
        _restoring = true;
        _operations++;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(request.Token);
            entered = true;
            while (true)
            {
                request.Token.ThrowIfCancellationRequested();
                if (_disposed || _unavailable || generation != _generation || _activeGeneration != generation) return;
                int count = _find.MatchCount, current = _find.ActiveMatchIndex;
                if (matchNumber > count || current < 1 || current > count || current == matchNumber)
                { PublishNativeState(); return; }

                var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<object> onMoved = (_, _) => moved.TrySetResult();
                // Register first: the runtime can complete a move synchronously.
                _find.ActiveMatchIndexChanged += onMoved;
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    if (_disposed || generation != _generation || _activeGeneration != generation) return;
                    long forward = ((long)matchNumber - current + count) % count;
                    long backward = ((long)current - matchNumber + count) % count;
                    if (backward < forward) _find.FindPrevious(); else _find.FindNext();
                    if (_find.ActiveMatchIndex != current) moved.TrySetResult();
                    await moved.Task.WaitAsync(request.Token);
                }
                finally
                {
                    try { _find.ActiveMatchIndexChanged -= onMoved; }
                    catch (Exception error) when (error is NotImplementedException or InvalidOperationException or COMException) { }
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            bool timedOut = !token.IsCancellationRequested && !_lifetime.IsCancellationRequested && generation == _generation;
            if (generation == _generation && !_disposed)
            {
                _unavailable |= timedOut;
                Stop();
            }
            if (timedOut) throw new TimeoutException("Restoring the browser Find match took too long. Reopen the preview to retry.");
            throw;
        }
        catch
        {
            if (generation == _generation && !_disposed) { _unavailable = true; Stop(); }
            throw;
        }
        finally
        {
            if (entered) _gate.Release();
            if (ReferenceEquals(_request, request)) _request = null;
            _restoring = false;
            _operations--;
            DisposeManagedWhenIdle();
        }
    }

    public void Stop()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        ++_generation; _activeGeneration = -1;
        _request?.Cancel();
        StopNative();
        MatchCount = 0; ActiveMatchIndex = -1; IsSearching = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnNativeChanged(object? sender, object args)
    {
        if (_disposed || _unavailable || IsSearching || _activeGeneration != _generation) return;
        try { PublishNativeState(); }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or COMException)
        {
            _unavailable = true;
            Stop();
        }
    }

    private void PublishNativeState()
    {
        MatchCount = _find.MatchCount;
        ActiveMatchIndex = _find.ActiveMatchIndex;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopNative()
    {
        try { _find.Stop(); }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or COMException)
        { _unavailable = true; }
    }

    private async Task ReleaseAfterNativeAsync(Task native)
    {
        try { await native; }
        catch (Exception) { /* The canceled request no longer owns visible state. */ }
        finally
        {
            _gate.Release();
            _operations--;
            DisposeManagedWhenIdle();
        }
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true; ++_generation; _activeGeneration = -1;
        try { _find.MatchCountChanged -= OnNativeChanged; }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or COMException) { }
        try { _find.ActiveMatchIndexChanged -= OnNativeChanged; }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or COMException) { }
        _lifetime.Cancel();
        _request?.Cancel();
        StopNative();
        MatchCount = 0; ActiveMatchIndex = -1; IsSearching = false;
        Changed = null;
        DisposeManagedWhenIdle();
    }

    private void DisposeManagedWhenIdle()
    {
        if (!_disposed || _operations != 0 || _managedDisposed) return;
        _managedDisposed = true;
        _gate.Dispose();
        _lifetime.Dispose();
    }
}
