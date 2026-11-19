using System.Windows;
using System.Windows.Threading;
using EstreamAssistant.App.Services;
using EstreamAssistant.App.ViewModels;
using EstreamAssistant.App.Views;
using EstreamAssistant.Core.Changes;
using EstreamAssistant.Core.Claude;
using EstreamAssistant.Core.Counts;
using EstreamAssistant.Core.Data;
using EstreamAssistant.Core.Logging;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppLog? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one copy at a time: two apps driving eStream at once would be unsafe.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\eStreamAssistant.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("eStream Assistant is already open.", "eStream Assistant",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();
        _log = new AppLog(paths.LogsFolder);
        _log.Info($"Starting eStream Assistant {AppVersion.Display}");

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var settings = AppSettings.Load(paths.SettingsFile);
        if (!File.Exists(paths.SettingsFile)) settings.Save(paths.SettingsFile);

        IItemRepository repo = CreateRepository(settings);
        var cache = new NameCache(paths.NameCacheFile(settings.DataSource));
        var lookup = new ItemLookupService(repo, cache)
        {
            CacheMaxAge = TimeSpan.FromHours(Math.Max(1, settings.NameCacheMaxAgeHours)),
        };

        var secrets = new DpapiSecretStore(paths.ApiKeyFile);
        var settingsVm = new SettingsViewModel(settings, paths, secrets, _log);
        var usage = new UsageTracker(paths.UsageFile);
        IChangeExecutor executor = settings.Executor == ExecutorKind.PowerShell
            ? new PowerShellChangeExecutor(settings.EngineFolder, paths.RunsFolder, _log)
            {
                BaseTimeout = TimeSpan.FromSeconds(Math.Max(10, settings.ScriptTimeoutBaseSeconds)),
                PerItemTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.ScriptTimeoutPerItemSeconds)),
            }
            : new MockChangeExecutor(repo, Path.Combine(paths.Root, "mock-engine", "Done"))
            {
                BaseDelay = TimeSpan.FromSeconds(2.5),
                PerItemDelay = TimeSpan.FromSeconds(1.5),
                UnverifiedUpcs = DemoUpcs(e.Args, "--mock-unverified"),
                FailUpcs = DemoUpcs(e.Args, "--mock-fail"),
            };
        _log.Info($"Dry runs: {executor.Name}.");
        var interrupted = PendingChanges.FindInterruptedSave(paths.RunsFolder);
        var pending = new PendingChanges(lookup, executor, _log, paths.RunsFolder);
        var history = new HistoryService(
            executor.ResultsFolder is { } rf ? [rf] : [],
            paths.RunsFolder,
            executor.DrivesEStream ? Path.Combine(settings.EngineFolder, "Logs") : paths.LogsFolder);
        IStockSheetExecutor sheetExecutor = settings.Executor == ExecutorKind.PowerShell
            ? new PowerShellStockSheetExecutor(settings.EngineFolder, paths.RunsFolder, _log)
            : new MockStockSheetExecutor(repo);
        var counts = new StockCounts(lookup, sheetExecutor, () => settings.ProtectedSheets, paths.Root, _log);
        var tools = new ToolExecutor(lookup, settings, pending, counts);
        IChatService chat = e.Args.Contains("--demo-chat")
            ? new DemoChatService(tools)
            : new ClaudeChatService(secrets.GetApiKey, settings, tools, usage, _log);
        _log.Info($"Chat: {chat.GetType().Name}, model {settings.Model}, effort {settings.Effort}, prompt {ClaudeChatService.SystemPromptVersion}.");
        var mainVm = new MainViewModel(lookup, settings, secrets, _log, new EStreamMonitor(), settingsVm, chat, usage, pending)
        {
            History = new HistoryViewModel(history),
            Counts = new CountsViewModel(counts, lookup, _log),
        };
        counts.WorkStarted += (_, what) => mainVm.ShowWorking(what, !counts.IsMock);
        counts.WorkFinished += (_, _) => mainVm.HideWorking();
        counts.EngineBlocker = () => !counts.IsMock && mainVm.EStreamStatus == StatusLevel.Bad
            ? "eStream Office isn't open. Open it, then try again." : null;

        var window = new MainWindow(mainVm, settings);
        MainWindow = window;
        window.Show();

        await mainVm.InitializeAsync();
        if (interrupted is not null)
        {
            _log.Warn($"Found a save that didn't finish (started {interrupted.Started}): {interrupted.JournalFile}");
            mainVm.ReportInterruptedSave(interrupted);
        }
        if (e.Args.Length > 0) await RunDemoAsync(e.Args, mainVm, window, pending);
    }

    /// <summary>
    /// Dev/demo only: scripted input for screenshots and quick checks, e.g.
    ///   EstreamAssistant.exe --search "lays cucumber" --pick 2 --scan 060410079591 --tab 3
    /// </summary>
    private async Task RunDemoAsync(string[] args, MainViewModel vm, MainWindow window, PendingChanges pending)
    {
        _log!.Info("Demo args: " + string.Join(' ', args));
        for (int i = 0; i < args.Length; i += 2)
        {
            await Task.Delay(400);
            var value = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--search":
                case "--pick":
                    vm.ScanText = value;
                    await vm.SubmitScanBoxCommand.ExecuteAsync(null);
                    break;
                case "--scan":
                    await vm.HandleScanAsync(value);
                    break;
                case "--chat":
                    vm.ChatText = value;
                    await vm.SendChatCommand.ExecuteAsync(null);
                    break;
                case "--choose" when int.TryParse(value, out var n):
                    await vm.ChooseNumberAsync(n);
                    break;
                case "--stage":
                    // --stage "060410079591:price=4.79,for=1"
                    var parts = value.Split(':', 2);
                    var req = new ChangeRequest { Upc = parts[0], Reason = "demo" };
                    foreach (var kv in (parts.Length > 1 ? parts[1] : "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = kv.Split('=');
                        var d = decimal.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                        req = p[0] switch
                        {
                            "price" => req with { Price = d },
                            "for" => req with { For = d },
                            "casecost" => req with { CaseCost = d },
                            "onhand" => req with { OnHand = d },
                            "min" => req with { Min = d },
                            "max" => req with { Max = d },
                            "deposit" => req with { Deposit = (int)d },
                            _ => req,
                        };
                    }
                    await pending.StageAsync([req]);
                    break;
                case "--save":
                    // Demo only: confirms the dialog automatically.
                    i--;
                    vm.ConfirmSave = _ => true;
                    while (vm.IsWorking) await Task.Delay(200);
                    await vm.SaveCommand.ExecuteAsync(null);
                    break;
                case "--save-dialog":
                    // Demo only: shows the real confirmation dialog (nothing is clicked).
                    i--;
                    _ = vm.SaveCommand.ExecuteAsync(null);
                    break;
                case "--count":
                    // --count "060410079591=12"  (adds to the count list)
                    var c = value.Split('=');
                    await vm.Counts!.HandleScanAsync(c[0]);
                    vm.Counts.CountText = c[1];
                    await vm.Counts.AddCountCommand.ExecuteAsync(null);
                    break;
                case "--make-sheet":
                    // Demo only: confirms the "make stock sheet" dialog automatically.
                    vm.Counts!.Comment = value;
                    var realConfirm = vm.Counts.ConfirmAction;
                    vm.Counts.ConfirmAction = (_, _, _, _, mustType) => mustType is null ? "" : null;
                    await vm.Counts.CreateSheetCommand.ExecuteAsync(null);
                    vm.Counts.ConfirmAction = realConfirm;
                    break;
                case "--post-first":
                    i--;
                    _ = vm.Counts!.PostCommand.ExecuteAsync(vm.Counts.Sheets.FirstOrDefault());
                    break;
                case "--mock-unverified":
                case "--mock-fail":
                    break;
                case "--dryrun":
                    i--; // flag without a value
                    await vm.DryRunCommand.ExecuteAsync(null);
                    break;
                case "--edit":
                    i--;
                    vm.StartEditCommand.Execute(null);
                    break;
                case "--demo-chat":
                    i--; // flag without a value
                    break;
                case "--tab" when int.TryParse(value, out var tab):
                    window.Tabs.SelectedIndex = tab;
                    break;
            }
        }
    }

    private static HashSet<long> DemoUpcs(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length
            ? args[i + 1].Split(',').Select(long.Parse).ToHashSet()
            : [];
    }

    private IItemRepository CreateRepository(AppSettings settings)
    {
        if (settings.DataSource == DataSourceKind.Sql)
        {
            _log!.Info("Data source: eStream SQL (read-only).");
            return new SqlItemRepository(settings.ConnectionString);
        }

        if (settings.SampleDataPath is { Length: > 0 } samplePath && File.Exists(samplePath))
        {
            try
            {
                var items = MockItemRepository.LoadSqlcmdExport(samplePath);
                _log!.Info($"Data source: mock, {items.Count} items from {samplePath}.");
                return new MockItemRepository(items, sourceName: "Sample export");
            }
            catch (Exception ex)
            {
                _log!.Error("Loading sample export failed; using built-in sample items", ex);
            }
        }

        _log!.Info("Data source: built-in mock items.");
        return new MockItemRepository();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show("Something went wrong. The details were saved to the app log.\n\n" + e.Exception.Message,
            "eStream Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("Exiting.");
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
