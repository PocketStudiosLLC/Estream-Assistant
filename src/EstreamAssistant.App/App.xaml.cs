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
using EstreamAssistant.Core.Receipts;
using EstreamAssistant.Core.Reports;
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
        _log = new AppLog(paths.LogsFolder) { Issues = new IssueLog(paths.IssuesFile) };
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
        var usage = new UsageTracker(paths.UsageFile, m => settings.ModelPrices.GetValueOrDefault(m));
        ScriptRunner.Log = _log.Info;
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
        lookup.LogError = (what, ex) => _log.Error(what, ex);
        var labels = new EstreamAssistant.Core.Labels.LabelQueue(paths.Root, _log);
        var mockDone = Path.Combine(paths.Root, "mock-engine", "Done");
        bool real = settings.Executor == ExecutorKind.PowerShell;
        // Phase C: price batch engine for bulk price / For / case cost changes (Import- and Apply-EsPriceBatch.ps1).
        IPriceBatchExecutor priceBatches = real
            ? new PowerShellPriceBatchExecutor(settings.EngineFolder, paths.RunsFolder, _log)
            : new MockPriceBatchExecutor(repo, mockDone) { Delay = TimeSpan.FromSeconds(3) };
        var pending = new PendingChanges(lookup, executor, _log, paths.RunsFolder)
        {
            Labels = labels,
            PriceBatches = priceBatches,
            ProtectedBatches = () => settings.ProtectedBatches,
        };
        _log.Info(priceBatches.IsAvailable
            ? $"Price batches: {priceBatches.Name}, for {pending.PriceBatchMinItems}+ price/cost-only rows (automatic)."
            : "Price batches: off (Import-/Apply-EsPriceBatch.ps1 not in the engine folder); every change uses the item editor.");
        var history = new HistoryService(
            executor.ResultsFolder is { } rf ? [rf] : [],
            paths.RunsFolder,
            executor.DrivesEStream ? Path.Combine(settings.EngineFolder, "Logs") : paths.LogsFolder);
        IStockSheetExecutor sheetExecutor = settings.Executor == ExecutorKind.PowerShell
            ? new PowerShellStockSheetExecutor(settings.EngineFolder, paths.RunsFolder, _log)
            : new MockStockSheetExecutor(repo);
        var counts = new StockCounts(lookup, sheetExecutor, () => settings.ProtectedSheets, paths.Root, _log);
        var receipt = new ReceiptSession(lookup, () => settings.PriceEndings);
        // F5.1/F5.8: the engine's Receipts folder on the store PC; a private one on the dev PC.
        var receiptInbox = Directory.Exists(settings.EngineFolder)
            ? Path.Combine(settings.EngineFolder, "Receipts")
            : Directory.CreateDirectory(Path.Combine(paths.Root, "mock-engine", "Receipts")).FullName;
        var receiptFolder = new ReceiptFolder(receiptInbox, _log);
        var reports = new ReportService(lookup, () => settings.ProtectedSheets, () => counts.Sheets, _log);
        // Phase C 3.4: queue the shelf label list in eStream (Add-EsLabelBatch.ps1).
        Core.Labels.ILabelExecutor labelExecutor = real
            ? new Core.Labels.PowerShellLabelExecutor(settings.EngineFolder, paths.RunsFolder, _log)
            : new Core.Labels.MockLabelExecutor(repo) { Delay = TimeSpan.FromSeconds(2) };
        var eStreamLabels = new Core.Labels.EStreamLabels(labels, labelExecutor, () => settings.LabelDesign, _log);
        var tools = new ToolExecutor(lookup, settings, pending, counts, receipt, reports, labels, eStreamLabels);
        IChatService chat = e.Args.Contains("--demo-chat")
            ? new DemoChatService(tools)
            : new ClaudeChatService(secrets.GetApiKey, settings, tools, usage, _log);
        if (chat is ClaudeChatService claude)
            claude.StateSnapshot = () => AppStateNote.Describe(pending, counts, receipt, pending.PriceBatchMinItems);
        _log.Info($"Chat: {chat.GetType().Name}, chat model {settings.Model} ({settings.Effort}), receipt model {settings.ReceiptModel} ({settings.ReceiptEffort}), prompt {ClaudeChatService.SystemPromptVersion}.");
        var mainVm = new MainViewModel(lookup, settings, secrets, _log, new EStreamMonitor(), settingsVm, chat, usage, pending)
        {
            History = new HistoryViewModel(history),
            Counts = new CountsViewModel(counts, lookup, _log),
            Reports = new ReportsViewModel(reports, _log, labels, eStreamLabels),
        };
        mainVm.UseReceipts(receipt, new ReceiptIngest(new WpfImageShrinker()), receiptFolder, counts);
        _log.Info(receiptFolder.Start() ? $"Watching {receiptFolder.Inbox} for receipts." : $"No receipts folder at {receiptFolder.Inbox}; not watching.");
        counts.WorkStarted += (_, what) => mainVm.ShowWorking(what, !counts.IsMock);
        counts.WorkFinished += (_, _) => mainVm.HideWorking();
        counts.EngineBlocker = () => !counts.IsMock && mainVm.EStreamStatus == StatusLevel.Bad
            ? "eStream Office isn't open. Open it, then try again." : null;
        eStreamLabels.WorkStarted += (_, what) => mainVm.ShowWorking(what, !eStreamLabels.IsMock);
        eStreamLabels.WorkFinished += (_, _) => mainVm.HideWorking();
        eStreamLabels.EngineBlocker = () => !eStreamLabels.IsMock && mainVm.EStreamStatus == StatusLevel.Bad
            ? "eStream Office isn't open. Open it, then try again." : null;

        var window = new MainWindow(mainVm, settings);
        MainWindow = window;
        window.Show();

        await mainVm.InitializeAsync();
        await mainVm.AttachWaitingReceiptsAsync();
        if (interrupted is not null)
        {
            _log.Warn($"Found a save that didn't finish (started {interrupted.Started}): {interrupted.JournalFile}");
            mainVm.ReportInterruptedSave(interrupted);
        }
        // Writes the real columns to the log for any feature whose tables differ from what its queries expect.
        _ = Task.Run(async () =>
        {
            foreach (var f in Enum.GetValues<DbFeature>())
            {
                try { await repo.CheckTablesAsync(f); }
                catch (Exception ex) { _log.Warn($"Couldn't check the {f} tables: " + ex.Message); }
            }
        });
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
                case "--attach":
                    // --attach "C:\path\receipt.jpg"  (puts it in the tray, like a drop)
                    await vm.AttachPathsAsync([value], ReceiptOrigin.File);
                    break;
                case "--send":
                    i--; // presses Send (sends the tray with "Read this receipt.")
                    await vm.SendChatCommand.ExecuteAsync(null);
                    break;
                case "--pick-line":
                    // --pick-line 2  (picks the first candidate for receipt line 2)
                    var lineVm = vm.ReceiptLines.FirstOrDefault(l => l.Number == int.Parse(value));
                    if (lineVm?.Candidates.FirstOrDefault() is { } cand) await vm.ChooseReceiptCandidateCommand.ExecuteAsync(cand);
                    break;
                case "--stage-receipt":
                    i--; // presses "Add ticked to pending" in the receipt table
                    await vm.StageReceiptCommand.ExecuteAsync(null);
                    break;
                case "--demo-chat":
                    i--; // flag without a value
                    break;
                case "--report":
                    // --report negative|zero|below_min|above_max|sheets|<sheet number>  (shows it on the Reports tab)
                    var rv = vm.Reports!;
                    if (int.TryParse(value, out var sheetNo)) { await rv.OpenSheetAsync(sheetNo); break; }
                    rv.SelectedChoice = rv.Choices.FirstOrDefault(c => value switch
                    {
                        "negative" => c.Filter == Core.Models.StockFilter.Negative,
                        "zero" => c.Filter == Core.Models.StockFilter.Zero,
                        "below_min" => c.Filter == Core.Models.StockFilter.BelowMin,
                        "above_max" => c.Filter == Core.Models.StockFilter.AboveMax,
                        _ => c.Filter is null,
                    }) ?? rv.Choices[0];
                    rv.SheetText = "";
                    await rv.RunCommand.ExecuteAsync(null);
                    break;
                case "--colors":
                    i--; // flag without a value
                    vm.Reports!.ShowColors = true;
                    break;
                case "--bugs":
                    // --bugs "message": adds a demo problem to the Bugs list, then opens the window.
                    vm.Issues?.Add("Demo", value);
                    window.Dispatcher.InvokeAsync(() => window.ShowBugs());
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
            return new SqlItemRepository(settings.ConnectionString) { Log = _log!.Info };
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
