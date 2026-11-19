using System.Windows;
using System.Windows.Threading;
using EstreamAssistant.App.Services;
using EstreamAssistant.App.ViewModels;
using EstreamAssistant.App.Views;
using EstreamAssistant.Core.Claude;
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
        var tools = new ToolExecutor(lookup, settings);
        IChatService chat = e.Args.Contains("--demo-chat")
            ? new DemoChatService(tools)
            : new ClaudeChatService(secrets.GetApiKey, settings, tools, usage, _log);
        _log.Info($"Chat: {chat.GetType().Name}, model {settings.Model}, effort {settings.Effort}, prompt {ClaudeChatService.SystemPromptVersion}.");
        var mainVm = new MainViewModel(lookup, settings, secrets, _log, new EStreamMonitor(), settingsVm, chat, usage);

        var window = new MainWindow(mainVm, settings);
        MainWindow = window;
        window.Show();

        await mainVm.InitializeAsync();
        if (e.Args.Length > 0) await RunDemoAsync(e.Args, mainVm, window);
    }

    /// <summary>
    /// Dev/demo only: scripted input for screenshots and quick checks, e.g.
    ///   EstreamAssistant.exe --search "lays cucumber" --pick 2 --scan 060410079591 --tab 3
    /// </summary>
    private async Task RunDemoAsync(string[] args, MainViewModel vm, MainWindow window)
    {
        _log!.Info("Demo args: " + string.Join(' ', args));
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            await Task.Delay(400);
            var value = args[i + 1];
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
                case "--demo-chat":
                    i--; // flag without a value
                    break;
                case "--tab" when int.TryParse(value, out var tab):
                    window.Tabs.SelectedIndex = tab;
                    break;
            }
        }
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
