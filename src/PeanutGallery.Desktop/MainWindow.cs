using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using PeanutGallery.Core;
using PeanutGallery.Desktop.Model;
using PeanutGallery.Desktop.Services;
using PeanutGallery.Desktop.Views;
using PeanutGallery.Engine;

namespace PeanutGallery.Desktop;

public class MainWindow : Window
{
    private const int AutoReviewCadenceSeconds = 90;

    private readonly DesktopConfig _config;
    private readonly DesktopStateStore _store = new();
    private readonly AutoReviewService _autoReview = new();
    private DesktopState _state;
    private WorkspaceSnapshot? _live;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _pollerCts;

    // Composition root: the per-call timeout is read once here (not in the orchestrator), and the
    // concrete reviewer is chosen here, so lower layers stay free of the environment and the Engine.
    private readonly Func<PeanutConfig, IReviewer> _reviewerFactory;

    public MainWindow()
    {
        Title = "Peanut Gallery";
        Width = 1120;
        Height = 740;
        MinWidth = 820;
        MinHeight = 520;
        Background = Palette.Bg;

        _config = DesktopConfig.Discover();
        var timeout = ReviewBudget.Parse(Environment.GetEnvironmentVariable(ReviewBudget.TimeoutVariable));
        _reviewerFactory = config => new ChatClientReviewer(config.Providers, perCallTimeout: timeout);

        // First run seeds tracked repos from env/desktop.json so existing setups keep working;
        // thereafter the persisted state is the source of truth (managed in-app).
        _state = _store.Exists ? _store.Load() : DesktopState.Seed(DesktopConfig.DiscoverSeedRepos());
        if (!_store.Exists) _store.Save(_state);

        Render();
        SyncPoller();
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        if (!_config.HasToken)
        {
            // Personas are local (no token needed), so keep that nav action live even offline.
            Content = Placeholder.WithBanner(
                Shell.Build(SampleData.Snapshot(now), now, new ShellCallbacks(OnOpenPersonas: OnOpenPersonas)),
                NoTokenHint(), Palette.Amber);
        }
        else if (_state.Repos.Count == 0)
        {
            // Token but no repos: render an empty shell so the add-repo input is available.
            Content = Shell.Build(SnapshotBuilder.Build([], null), now, Callbacks());
        }
        else
        {
            Content = Placeholder.Centered("Loading…", "Fetching open pull requests from GitHub.");
            _ = LoadLiveAsync();
        }
    }

    // Fire-and-forget is safe: the whole body (incl. everything before the first await) is inside
    // the try/catch, so the discarded Task can never fault unobserved. A newer load cancels the
    // prior one so a slow in-flight fetch can't clobber the current view with stale data.
    private async Task LoadLiveAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var snapshot = await SnapshotService.LoadAsync(_config, _state.Repos, _state.Selected, _state.AutoReview, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                _live = snapshot;
                Content = Shell.Build(snapshot, DateTimeOffset.UtcNow, Callbacks());
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load; the newer one owns the view.
        }
        catch (Exception e)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                var now = DateTimeOffset.UtcNow;
                Content = Placeholder.WithBanner(
                    Shell.Build(SampleData.Snapshot(now), now),
                    "Live load failed — showing sample data. " + e.Message,
                    Palette.Red);
            });
        }
    }

    private ShellCallbacks Callbacks() => new(
        OnReview: OnReview,
        OnSelectRepo: OnSelectRepo,
        OnAddRepo: OnAddRepo,
        OnRemoveRepo: OnRemoveRepo,
        OnToggleAutoReview: OnToggleAutoReview,
        OnOpenPersonas: OnOpenPersonas);

    private PersonasWindow? _personasWindow;

    // Reuse one Personas window rather than spawning a new one on every nav click.
    private void OnOpenPersonas()
    {
        if (_personasWindow is not null)
        {
            _personasWindow.Activate();
            return;
        }

        _personasWindow = new PersonasWindow();
        _personasWindow.Closed += (_, _) => _personasWindow = null;
        _personasWindow.Show(this);
    }

    private void OnSelectRepo(string slug) => Mutate(_state.Select(slug));
    private void OnAddRepo(string slug) => Mutate(_state.AddRepo(slug));
    private void OnRemoveRepo(string slug) => Mutate(_state.RemoveRepo(slug));
    private void OnToggleAutoReview(string slug, bool on) => Mutate(_state.SetAutoReview(slug, on));

    // Apply a state transform, persist it, re-render, and re-sync the auto-review poller.
    private void Mutate(DesktopState next)
    {
        if (next == _state) return;
        _state = next;
        _store.Save(_state);
        Render();
        SyncPoller();
    }

    // Start the background auto-review poller when any repo is subscribed (and we have a token);
    // stop it otherwise. Cheap to re-sync on every state change.
    private void SyncPoller()
    {
        var shouldRun = _config.HasToken && _state.AutoReview.Count > 0;
        if (shouldRun && _pollerCts is null)
        {
            var cts = _pollerCts = new CancellationTokenSource();
            _ = PollLoopAsync(cts.Token);
        }
        else if (!shouldRun && _pollerCts is not null)
        {
            _pollerCts.Cancel();
            _pollerCts.Dispose();
            _pollerCts = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var posted = await _autoReview.RunCycleAsync(_config, _state.AutoReview, _reviewerFactory, Log, ct);
                if (posted > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => { if (!ct.IsCancellationRequested) _ = LoadLiveAsync(); });
                }

                await Task.Delay(TimeSpan.FromSeconds(AutoReviewCadenceSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log("auto-review: cycle error — " + e.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(AutoReviewCadenceSeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static void Log(string message) => System.Diagnostics.Debug.WriteLine("[peanut-gallery] " + message);

    protected override void OnClosed(EventArgs e)
    {
        _pollerCts?.Cancel();
        _loadCts?.Cancel();
        base.OnClosed(e);
    }

    // Open a one-shot review for the target PR; refresh the snapshot if it posts.
    private void OnReview(ReviewTarget target)
    {
        if (_config.Token is null) return;

        var window = new ReviewWindow(
            _config.Token, _config.ApiBaseUrl, target.Owner, target.Repo, target.Number, target.Title, _reviewerFactory);
        window.Closed += (_, _) =>
        {
            if (window.Posted)
            {
                Content = Placeholder.Centered("Refreshing…", "Reloading review status from GitHub.");
                _ = LoadLiveAsync();
            }
        };
        window.Show(this);
    }

    private static string NoTokenHint() =>
        "Showing sample data. Set GITHUB_TOKEN (or GITHUB_PAT) to load live, then add repositories in the sidebar.";
}
