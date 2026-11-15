using Microsoft.AspNetCore.Components;
using Spyglass.Collector.Ui.Services;

namespace Spyglass.Collector.Ui.Shared;

/// <summary>
/// The load pattern of the data pages (UD-2, 11, 16, 21): <see cref="LoadAsync"/> runs on every parameter change and, optionally,
/// every <see cref="RefreshEvery"/>. A changed parameter cancels the load in flight and a stale result is dropped; a refresh tick
/// does not (it waits for the load to finish, so a response slower than the interval still arrives, FC-16) unless the load has
/// run past <see cref="LoadTimeout"/>, which is cancelled and reported. A failure leaves the
/// previous data on screen with <see cref="Error"/> set for an <c>ErrorBanner</c>.
/// Implementations must call <c>ct.ThrowIfCancellationRequested()</c> after their last await and before they assign page state.
/// </summary>
public abstract class LoadPage : ComponentBase, IDisposable
{
    [Inject] protected Api Api { get; set; } = null!;

    private CancellationTokenSource? _load;
    private long _loadStartedTick;
    /// <summary>A load still running after this long is abandoned by the next refresh tick and an error is shown.</summary>
    protected virtual TimeSpan LoadTimeout => TimeSpan.FromSeconds(60);
    private readonly CancellationTokenSource _life = new();

    /// <summary>True after the first load that finished, successfully or not.</summary>
    protected bool Loaded { get; private set; }
    protected string? Error { get; set; }

    protected abstract Task LoadAsync(CancellationToken ct);
    protected virtual TimeSpan? RefreshEvery => null;

    protected override void OnInitialized()
    {
        if (RefreshEvery is { } every) _ = Tick(every, _life.Token);
    }

    protected override Task OnParametersSetAsync() => Reload();

    private async Task Tick(TimeSpan every, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_load is { } running)
                {
                    if (Environment.TickCount64 - _loadStartedTick < LoadTimeout.TotalMilliseconds) continue; // let it finish
                    await running.CancelAsync();
                    Error = $"The collector did not answer within {LoadTimeout.TotalSeconds:F0} s";
                    Loaded = true;
                    await InvokeAsync(StateHasChanged);
                }
                await Reload();
            }
        }
        catch (OperationCanceledException) { }
    }

    protected async Task Reload()
    {
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        _loadStartedTick = Environment.TickCount64;
        var ct = cts.Token;
        try
        {
            await Api.EnsureClock();
            await LoadAsync(ct);
            ct.ThrowIfCancellationRequested();
            Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _life.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            Error = ex is HttpRequestException ? $"The collector did not answer ({ex.Message})" : ex.Message;
        }
        finally { if (ReferenceEquals(_load, cts)) _load = null; }
        Loaded = true;
        await InvokeAsync(StateHasChanged);
    }

    public virtual void Dispose()
    {
        _life.Cancel();
        _load?.Cancel();
        GC.SuppressFinalize(this);
    }
}
