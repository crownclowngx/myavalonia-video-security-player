using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Mvvm.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.Business.Docking;
using MyAvaloniaManagement.Business.Documents;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Constants;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Playback.IntegrationHarness;

/// <summary>
/// G8 P1 真实窗口验收套件。
/// </summary>
/// <remarks>
/// 本套件只编排已经存在的产品入口，不复制加解密、目录监听或播放器状态机。规模正确性和
/// 可控故障由 xUnit 固定；这里专注真实 Avalonia、Dock、Document Scope、虚拟化和 HWND。
/// </remarks>
internal sealed class G8P1AcceptanceSuite(
    IServiceProvider services,
    HarnessOptions options) : IAcceptanceSuite
{
    private const int FullscreenCycles = 10;
    private const int DockSwitches = 50;

    private readonly string _runCanary = ResolveRunCanary();
    private readonly List<string> _failedScenarioCodes = [];
    private readonly Dictionary<string, long> _stageDurationsMs = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private int _maximumVisibleContainers;
    private long _maximumHeartbeatGapMs;

    private string PlayerPasswordA => $"G8-PASSWORD-A-{_runCanary}!";
    private string PlayerPasswordB => $"G8-PASSWORD-B-{_runCanary}!";
    private string QueuePasswordA => $"G8-QUEUE-A-{_runCanary}!";
    private string QueuePasswordB => $"G8-QUEUE-B-{_runCanary}!";
    private string PublicDescriptionCanary =>
        $"G8-PUBLIC-DESCRIPTION-{_runCanary}";

    public async Task<int> RunAsync()
    {
        G8P1Report report;
        try
        {
            await WaitUntilAsync(
                () => Application.Current?.ApplicationLifetime is
                    IClassicDesktopStyleApplicationLifetime { MainWindow: not null },
                TimeSpan.FromSeconds(10),
                "G8-HOST-START");

            using var workspace = AcceptanceWorkspace.Create("g8");
            var assets = await MeasureAsync(
                "assetPreparation",
                () => Task.Run(() => PrepareAssetsAsync(workspace)));

            var mainWindow = (Application.Current!.ApplicationLifetime as
                IClassicDesktopStyleApplicationLifetime)!.MainWindow!;
            var documentCoordinator = services
                .GetRequiredService<DocumentPersistenceCoordinator>();
            var hostWorkspace = services.GetRequiredService<MyAvaloniaManagement.Business.Workspace.WorkspaceSession>();
            var documentDock = hostWorkspace.DockFactory.GetDockable<Dock.Model.Controls.IDocumentDock>(
                    MyAvaloniaManagement.Business.Layout.DockLayoutIds.Documents)
                as DocumentDock ?? throw new AcceptanceException("G8-HOST-DOCK");

            var documents = await MeasureAsync(
                "documentComposition",
                () => CreateDocumentSetAsync(documentCoordinator, documentDock));
            try
            {
                await MeasureAsync(
                    "queuePresentation",
                    () => VerifyQueuePresentationAsync(
                        mainWindow,
                        documentDock,
                        documents,
                        assets));
                await MeasureAsync(
                    "libraryScale",
                    () => VerifyLibraryScaleAsync(
                        mainWindow,
                        documentDock,
                        documents.LibraryA,
                        assets.LibraryRoot));
                await MeasureAsync(
                    "documentIsolation",
                    () => VerifyDocumentIsolationAsync(
                        documentDock,
                        documents,
                        assets.EncryptedMedia));
                await MeasureAsync(
                    "playbackComposition",
                    () => VerifyPlaybackCompositionAsync(
                        documentDock,
                        documents,
                        assets.EncryptedMedia));
            }
            finally
            {
                foreach (var document in documents.All.Reverse())
                {
                    Console.WriteLine($"G8 stage: close-{document.Title}");
                    hostWorkspace.DockFactory.CloseDockable(document);
                    await DrainDispatcherAsync();
                }
            }

            var resources = SecurePlaybackDiagnostics.CaptureResources();
            Require(resources == default, "G8-RESOURCE-NOT-ZERO");
            Require(CountUnexpectedTopLevelWindows() == 0, "G8-UNEXPECTED-WINDOW");

            report = BuildReport(
                resources,
                options.QueueItems,
                options.LibraryItems);
        }
        catch (AcceptanceException ex)
        {
            _failedScenarioCodes.Add(ex.Code);
            report = BuildReport(
                SecurePlaybackDiagnostics.CaptureResources(),
                options.QueueItems,
                options.LibraryItems);
        }
        catch (Exception ex)
        {
            // 报告只保存异常类型形成的稳定代码，不把可能含路径的 Message 写入证据。
            _failedScenarioCodes.Add($"G8-UNEXPECTED-{ex.GetType().Name.ToUpperInvariant()}");
            report = BuildReport(
                SecurePlaybackDiagnostics.CaptureResources(),
                options.QueueItems,
                options.LibraryItems);
        }

        report = report with
        {
            Success = _failedScenarioCodes.Count == 0,
            FailedScenarioCodes = _failedScenarioCodes.Distinct().ToArray()
        };
        await WriteReportAsync(report);

        Console.WriteLine(
            report.Success
                ? "G8 P1 Windows x64 集成门禁通过。"
                : "G8 P1 Windows x64 集成门禁失败。");
        foreach (var code in report.FailedScenarioCodes)
            Console.Error.WriteLine($"- {code}");
        return report.Success ? 0 : 1;
    }

    private async Task<G8Assets> PrepareAssetsAsync(AcceptanceWorkspace workspace)
    {
        var source = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "RealMedia",
            "synthetic-av-short.mp4");
        if (!File.Exists(source))
            throw new AcceptanceException("G8-ASSET-SOURCE-MISSING");

        var encrypted = workspace.Resolve("fixture.secvid");
        await new Secvid03Encryptor().EncryptAsync(
            source,
            encrypted,
            PlayerPasswordA,
            "G8 fixture",
            PublicDescriptionCanary);

        var encryptionA = workspace.CopyMany(
            source,
            "queues/encryption-a",
            options.QueueItems,
            ".mp4");
        var encryptionB = workspace.CopyMany(
            source,
            "queues/encryption-b",
            Math.Min(3, options.QueueItems),
            ".mp4");
        var decryptionA = workspace.CopyMany(
            encrypted,
            "queues/decryption-a",
            options.QueueItems,
            ".secvid");
        var decryptionB = workspace.CopyMany(
            encrypted,
            "queues/decryption-b",
            Math.Min(3, options.QueueItems),
            ".secvid");
        var decryptionOutputA = workspace.CreateDirectory("queues/decrypted-a");
        var decryptionOutputB = workspace.CreateDirectory("queues/decrypted-b");

        var libraryRoot = workspace.CreateDirectory("library");
        for (var index = 0; index < options.LibraryItems; index++)
        {
            var level = (index % 3) switch
            {
                0 => "level-a",
                1 => "level-a/level-b",
                _ => "level-a/level-b/level-c"
            };
            var directory = workspace.CreateDirectory($"library/{level}");
            File.Copy(
                encrypted,
                Path.Combine(directory, $"{index:D4}.secvid"),
                overwrite: false);
        }

        return new G8Assets(
            encrypted,
            encryptionA,
            encryptionB,
            decryptionA,
            decryptionB,
            decryptionOutputA,
            decryptionOutputB,
            libraryRoot);
    }

    private static async Task<G8DocumentSet> CreateDocumentSetAsync(
        DocumentPersistenceCoordinator documentCoordinator,
        DocumentDock documentDock)
    {
        var encryptorA = await CreateDocumentAsync<VideoEncryptorViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.VideoEncryptorDocument,
            "G8-ENC-A");
        var encryptorB = await CreateDocumentAsync<VideoEncryptorViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.VideoEncryptorDocument,
            "G8-ENC-B");
        var decryptorA = await CreateDocumentAsync<VideoDecryptorViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.VideoDecryptorDocument,
            "G8-DEC-A");
        var decryptorB = await CreateDocumentAsync<VideoDecryptorViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.VideoDecryptorDocument,
            "G8-DEC-B");
        var playerA = await CreateDocumentAsync<SecretVideoPlayerViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument,
            "G8-PLAYER-A");
        var playerB = await CreateDocumentAsync<SecretVideoPlayerViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.SecretVideoPlayerDocument,
            "G8-PLAYER-B");
        var libraryA = await CreateDocumentAsync<SecretVideoLibraryViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument,
            "G8-LIB-A");
        var libraryB = await CreateDocumentAsync<SecretVideoLibraryViewModel>(
            documentCoordinator,
            documentDock,
            VideoSecurityPlayerContributionIds.SecretVideoLibraryDocument,
            "G8-LIB-B");
        await DrainDispatcherAsync();
        return new G8DocumentSet(
            encryptorA,
            encryptorB,
            decryptorA,
            decryptorB,
            playerA,
            playerB,
            libraryA,
            libraryB);
    }

    private async Task VerifyQueuePresentationAsync(
        Window mainWindow,
        DocumentDock documentDock,
        G8DocumentSet documents,
        G8Assets assets)
    {
        documents.EncryptorA.Password = QueuePasswordA;
        documents.EncryptorA.ConfirmPassword = documents.EncryptorA.Password;
        documents.EncryptorB.Password = QueuePasswordB;
        documents.EncryptorB.ConfirmPassword = documents.EncryptorB.Password;
        documents.DecryptorA.Password = PlayerPasswordA;
        documents.DecryptorB.Password = PlayerPasswordB;
        documents.DecryptorA.SetOutputDirectory(assets.DecryptionOutputA);
        documents.DecryptorB.SetOutputDirectory(assets.DecryptionOutputB);

        await documents.EncryptorA.AddFilesAsync(assets.EncryptionInputsA);
        await documents.EncryptorB.AddFilesAsync(assets.EncryptionInputsB);
        await documents.DecryptorA.AddFilesAsync(assets.DecryptionInputsA);
        await documents.DecryptorB.AddFilesAsync(assets.DecryptionInputsB);

        Require(documents.EncryptorA.ItemCount == options.QueueItems, "G8-ENC-QUEUE-COUNT");
        Require(documents.DecryptorA.Items.Count == options.QueueItems, "G8-DEC-QUEUE-COUNT");
        Require(
            documents.EncryptorA.Password != documents.EncryptorB.Password,
            "G8-ENC-PASSWORD-ISOLATION");
        Require(
            documents.DecryptorA.Password != documents.DecryptorB.Password,
            "G8-DEC-PASSWORD-ISOLATION");

        foreach (var document in new object[]
                 {
                     documents.EncryptorA,
                     documents.DecryptorA
                 })
        {
            documentDock.ActiveDockable = FindDockable(documentDock, document);
            await DrainDispatcherAsync();
            var list = mainWindow.GetVisualDescendants()
                .OfType<ListBox>()
                .FirstOrDefault(candidate => candidate.ItemCount == options.QueueItems);
            Require(list is not null, "G8-QUEUE-LIST-NOT-RENDERED");
            if (list is null)
                continue;
            ScrollToSamples(list);
            _maximumVisibleContainers = Math.Max(
                _maximumVisibleContainers,
                list.GetVisualDescendants().OfType<ListBoxItem>().Count());
        }

        // 100 项列表不应一次创建 100 个视觉容器；该断言直接固定虚拟化仍在生效。
        Require(
            options.QueueItems < 20 || _maximumVisibleContainers < options.QueueItems,
            "G8-QUEUE-NOT-VIRTUALIZED");
    }

    private async Task VerifyLibraryScaleAsync(
        Window mainWindow,
        DocumentDock documentDock,
        SecretVideoLibraryViewModel library,
        string libraryRoot)
    {
        documentDock.ActiveDockable = FindDockable(documentDock, library);
        await DrainDispatcherAsync();
        library.Browser.IncludeSubdirectories = true;

        // 生产目录会话在初扫后继续持有 watcher，因此 LoadFolderAsync 的任务只会在换目录或
        // Document 关闭时结束。验收等待可观察的“初扫完成”事实，而不是错误等待会话退出。
        _ = library.OpenFolderAsync(libraryRoot);
        var heartbeat = await MeasureHeartbeatAsync(() =>
            WaitUntilAsync(
                () => !library.Browser.IsScanning &&
                      library.Browser.VisibleItemCount == options.LibraryItems,
                TimeSpan.FromSeconds(45),
                "G8-LIBRARY-INITIAL-SCAN"));
        RecordHeartbeat(heartbeat);

        foreach (var sort in Enum.GetValues<VideoLibrarySortField>())
        {
            library.Browser.SortField = sort;
            await Task.Delay(300);
            Require(
                library.Browser.VisibleItemCount == options.LibraryItems,
                "G8-LIBRARY-SORT-COUNT");
        }

        library.Browser.SearchText = $"{options.LibraryItems - 1:D4}";
        await Task.Delay(350);
        Require(library.Browser.VisibleItemCount == 1, "G8-LIBRARY-SEARCH");
        library.Browser.SearchText = string.Empty;
        await Task.Delay(350);

        var list = mainWindow.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(candidate => candidate.ItemCount == options.LibraryItems);
        Require(list is not null, "G8-LIBRARY-LIST-NOT-RENDERED");
        if (list is not null)
        {
            var scrollHeartbeat = await MeasureHeartbeatAsync(() =>
            {
                ScrollToSamples(list);
                return DrainDispatcherAsync();
            });
            RecordHeartbeat(scrollHeartbeat);
            _maximumVisibleContainers = Math.Max(
                _maximumVisibleContainers,
                list.GetVisualDescendants().OfType<ListBoxItem>().Count());
            Require(
                options.LibraryItems < 20 ||
                list.GetVisualDescendants().OfType<ListBoxItem>().Count() < options.LibraryItems,
                "G8-LIBRARY-NOT-VIRTUALIZED");
        }

        var added = Path.Combine(libraryRoot, "level-a", "g8-added.secvid");
        File.Copy(library.Browser.VisibleItems[0].FilePath, added);
        await WaitUntilAsync(
            () => library.Browser.VisibleItemCount == options.LibraryItems + 1,
            TimeSpan.FromSeconds(10),
            "G8-LIBRARY-WATCH-ADD");
        var renamed = Path.Combine(libraryRoot, "level-a", "g8-renamed.secvid");
        File.Move(added, renamed);
        await WaitUntilAsync(
            () => library.Browser.VisibleItems.Any(item =>
                      string.Equals(item.FilePath, renamed, StringComparison.OrdinalIgnoreCase)) &&
                  library.Browser.VisibleItems.All(item =>
                      !string.Equals(item.FilePath, added, StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromSeconds(10),
            "G8-LIBRARY-WATCH-RENAME");
        File.Delete(renamed);
        await WaitUntilAsync(
            () => library.Browser.VisibleItemCount == options.LibraryItems,
            TimeSpan.FromSeconds(10),
            "G8-LIBRARY-WATCH-DELETE");

        Require(
            library.Browser.VisibleItems
                .Select(item => item.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == options.LibraryItems,
            "G8-LIBRARY-DUPLICATE");
    }

    private Task VerifyDocumentIsolationAsync(
        DocumentDock documentDock,
        G8DocumentSet documents,
        string encryptedMedia)
    {
        Require(
            !ReferenceEquals(documents.PlayerA.PlayerViewModel, documents.PlayerB.PlayerViewModel),
            "G8-PLAYER-VM-SHARED");
        Require(
            !ReferenceEquals(documents.LibraryA.Browser, documents.LibraryB.Browser),
            "G8-LIBRARY-BROWSER-SHARED");
        Require(
            !ReferenceEquals(documents.LibraryA.PlayerViewModel, documents.LibraryB.PlayerViewModel),
            "G8-LIBRARY-PLAYER-SHARED");

        documents.PlayerA.Password = PlayerPasswordA;
        documents.PlayerB.Password = PlayerPasswordB;
        documents.PlayerA.FilePath = encryptedMedia;
        documents.PlayerB.FilePath = encryptedMedia;
        documents.LibraryA.Password = PlayerPasswordA;
        documents.LibraryB.Password = PlayerPasswordB;
        documentDock.ActiveDockable = FindDockable(documentDock, documents.PlayerA);

        Require(documents.PlayerA.Password == PlayerPasswordA, "G8-PLAYER-A-PASSWORD");
        Require(documents.PlayerB.Password == PlayerPasswordB, "G8-PLAYER-B-PASSWORD");
        Require(documents.LibraryA.Password != documents.LibraryB.Password, "G8-LIBRARY-PASSWORD");
        return Task.CompletedTask;
    }

    private async Task VerifyPlaybackCompositionAsync(
        DocumentDock documentDock,
        G8DocumentSet documents,
        string encryptedMedia)
    {
        documentDock.ActiveDockable = FindDockable(documentDock, documents.PlayerA);
        await WaitUntilAsync(
            () => documents.PlayerA.PlayerViewModel.IsVideoSurfaceReady,
            TimeSpan.FromSeconds(10),
            "G8-PLAYER-A-SURFACE");
        Require(
            await documents.PlayerA.PlayerViewModel.LoadAndPlayMediaAsync(
                encryptedMedia,
                PlayerPasswordA),
            "G8-PLAYER-A-LOAD");
        await WaitUntilAsync(
            () => documents.PlayerA.PlayerViewModel.PlaybackSnapshot.PositionMs > 100,
            TimeSpan.FromSeconds(8),
            "G8-PLAYER-A-PROGRESS");
        var playerAOutputGeneration =
            documents.PlayerA.PlayerViewModel.SurfaceSession?.VideoOutput.Generation ?? 0;
        Require(playerAOutputGeneration > 0, "G8-PLAYER-A-NATIVE");

        documentDock.ActiveDockable = FindDockable(documentDock, documents.PlayerB);
        await WaitUntilAsync(
            () => documents.PlayerB.PlayerViewModel.IsVideoSurfaceReady,
            TimeSpan.FromSeconds(10),
            "G8-PLAYER-B-SURFACE");
        Require(
            !await documents.PlayerB.PlayerViewModel.LoadMediaAsync(
                encryptedMedia,
                PlayerPasswordB),
            "G8-PLAYER-B-WRONG-PASSWORD");
        Require(
            documents.PlayerA.PlayerViewModel.PlaybackSnapshot.HasMedia,
            "G8-PLAYER-A-AFFECTED");

        documentDock.ActiveDockable = FindDockable(documentDock, documents.PlayerA);
        await WaitUntilAsync(
            () => documents.PlayerA.PlayerViewModel.IsVideoSurfaceReady,
            TimeSpan.FromSeconds(8),
            "G8-PLAYER-A-RESTORE");
        for (var index = 0; index < DockSwitches; index++)
        {
            documentDock.ActiveDockable = FindDockable(
                documentDock,
                index % 2 == 0 ? documents.LibraryB : documents.PlayerA);
            await DrainDispatcherAsync();
        }
        documentDock.ActiveDockable = FindDockable(documentDock, documents.PlayerA);
        await WaitUntilAsync(
            () => documents.PlayerA.PlayerViewModel.IsVideoSurfaceReady,
            TimeSpan.FromSeconds(8),
            "G8-DOCK-SWITCH-RESTORE");
        Require(
            playerAOutputGeneration ==
            documents.PlayerA.PlayerViewModel.SurfaceSession?.VideoOutput.Generation,
            "G8-DOCK-REPLACED-PLAYER");

        foreach (var rate in new[] { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f })
        {
            Require(
                (await documents.PlayerA.PlayerViewModel.SetPlaybackRateAsync(rate)).Success,
                "G8-PLAYBACK-RATE");
        }

        for (var cycle = 0; cycle < FullscreenCycles; cycle++)
        {
            documents.PlayerA.PlayerViewModel.ToggleFullscreenCommand.Execute(null);
            await WaitUntilAsync(
                () => documents.PlayerA.PlayerViewModel.IsFullscreen &&
                      !documents.PlayerA.PlayerViewModel.IsFullscreenTransitioning,
                TimeSpan.FromSeconds(8),
                "G8-FULLSCREEN-ENTER");
            documents.PlayerA.PlayerViewModel.ToggleFullscreenCommand.Execute(null);
            await WaitUntilAsync(
                () => !documents.PlayerA.PlayerViewModel.IsFullscreen &&
                      !documents.PlayerA.PlayerViewModel.IsFullscreenTransitioning &&
                      documents.PlayerA.PlayerViewModel.IsVideoSurfaceReady,
                TimeSpan.FromSeconds(8),
                "G8-FULLSCREEN-EXIT");
            Require(
                playerAOutputGeneration ==
                documents.PlayerA.PlayerViewModel.SurfaceSession?.VideoOutput.Generation,
                "G8-FULLSCREEN-REPLACED-PLAYER");
        }
    }

    private G8P1Report BuildReport(
        PlaybackResourceSnapshot resources,
        int queueItems,
        int libraryItems) =>
        new(
            1,
            "g8-p1-integration",
            _failedScenarioCodes.Count == 0,
            "windows-x64",
            Environment.Version.ToString(),
            queueItems,
            libraryItems,
            8,
            FullscreenCycles,
            DockSwitches,
            _maximumVisibleContainers,
            _maximumHeartbeatGapMs,
            resources,
            new Dictionary<string, long>(_stageDurationsMs),
            _elapsed.ElapsedMilliseconds,
            _failedScenarioCodes.Distinct().ToArray());

    private async Task<T> MeasureAsync<T>(string name, Func<Task<T>> action)
    {
        Console.WriteLine($"G8 stage: {name}-start");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            _stageDurationsMs[name] = stopwatch.ElapsedMilliseconds;
            Console.WriteLine($"G8 stage: {name}-end");
        }
    }

    private async Task MeasureAsync(string name, Func<Task> action)
    {
        Console.WriteLine($"G8 stage: {name}-start");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action();
        }
        finally
        {
            _stageDurationsMs[name] = stopwatch.ElapsedMilliseconds;
            Console.WriteLine($"G8 stage: {name}-end");
        }
    }

    private static async Task<UiHeartbeatResult> MeasureHeartbeatAsync(Func<Task> operation)
    {
        var clock = Stopwatch.StartNew();
        var previous = clock.ElapsedMilliseconds;
        var maximumGap = 0L;
        var activeTicks = 0;
        var active = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        EventHandler tick = (_, _) =>
        {
            var now = clock.ElapsedMilliseconds;
            maximumGap = Math.Max(maximumGap, now - previous);
            previous = now;
            if (active)
                activeTicks++;
        };
        timer.Tick += tick;
        try
        {
            timer.Start();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            active = true;
            var started = clock.ElapsedMilliseconds;
            await operation();
            active = false;
            await Task.Delay(30);
            return new UiHeartbeatResult(
                clock.ElapsedMilliseconds - started,
                activeTicks,
                maximumGap);
        }
        finally
        {
            timer.Stop();
            timer.Tick -= tick;
        }
    }

    private void RecordHeartbeat(UiHeartbeatResult heartbeat)
    {
        _maximumHeartbeatGapMs = Math.Max(
            _maximumHeartbeatGapMs,
            heartbeat.MaximumGapMs);
        Require(
            heartbeat.OperationElapsedMs < 10 || heartbeat.ActiveTicks > 0,
            "G8-UI-HEARTBEAT");
    }

    private static void ScrollToSamples(ListBox list)
    {
        if (list.ItemCount == 0)
            return;
        list.ScrollIntoView(list.Items[list.ItemCount - 1]!);
        list.ScrollIntoView(list.Items[list.ItemCount / 2]!);
        list.ScrollIntoView(list.Items[0]!);
    }

    private static async Task<HarnessDocument<T>> CreateDocumentAsync<T>(
        DocumentPersistenceCoordinator documentCoordinator,
        DocumentDock documentDock,
        MyAvaloniaManagement.PluginSdk.DocumentTypeId documentType,
        string title) where T : class
    {
        var creation = await documentCoordinator.CreateDocumentAsync(documentType);
        if (creation.ShouldUpdateError && !string.IsNullOrEmpty(creation.Error))
        {
            throw new AcceptanceException("G8-DOCUMENT-CREATE");
        }
        var dockable = documentDock.VisibleDockables?
            .OfType<ManagedDocumentDockable>()
            .LastOrDefault(item => item.Model is T) ??
            throw new AcceptanceException("G8-DOCUMENT-CREATE");
        dockable.Title = title;
        return new HarnessDocument<T>(dockable, (T)dockable.Model);
    }

    private static ManagedDocumentDockable FindDockable(
        DocumentDock documentDock,
        object model) =>
        documentDock.VisibleDockables?
            .OfType<ManagedDocumentDockable>()
            .SingleOrDefault(item => ReferenceEquals(item.Model, model)) ??
        throw new AcceptanceException("G8-DOCUMENT-ADAPTER-MISSING");

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string scenarioCode)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
                throw new AcceptanceException(scenarioCode);
            await Task.Delay(50);
        }
    }

    private static async Task DrainDispatcherAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(50);
    }

    private static int CountUnexpectedTopLevelWindows() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Count(window => window != desktop.MainWindow)
            : 0;

    private static string ResolveRunCanary()
    {
        // 统一脚本拥有轮次标识，套件只派生本轮敏感值；报告不保存该标识。
        // 直接运行 Harness 时使用随机值，避免开发者重复运行时复用密码。
        var value = Environment.GetEnvironmentVariable(
            "MYSMALLTOOLS_G8_RUN_CANARY");
        return string.IsNullOrWhiteSpace(value)
            ? Guid.NewGuid().ToString("N")
            : value;
    }

    private static void Require(bool condition, string scenarioCode)
    {
        if (!condition)
            throw new AcceptanceException(scenarioCode);
    }

    private async Task WriteReportAsync(G8P1Report report)
    {
        var reportPath = Path.GetFullPath(options.ReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var json = JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        // 证据文件统一使用 LF，避免 Windows 行尾被 Git 误判为尾随空白。
        await File.WriteAllTextAsync(reportPath, json.ReplaceLineEndings("\n"));
    }

    private sealed class AcceptanceException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}

internal sealed record G8Assets(
    string EncryptedMedia,
    IReadOnlyList<string> EncryptionInputsA,
    IReadOnlyList<string> EncryptionInputsB,
    IReadOnlyList<string> DecryptionInputsA,
    IReadOnlyList<string> DecryptionInputsB,
    string DecryptionOutputA,
    string DecryptionOutputB,
    string LibraryRoot);

internal sealed class G8DocumentSet
{
    private readonly Dictionary<object, ManagedDocumentDockable> _dockables;

    public G8DocumentSet(
        HarnessDocument<VideoEncryptorViewModel> encryptorA,
        HarnessDocument<VideoEncryptorViewModel> encryptorB,
        HarnessDocument<VideoDecryptorViewModel> decryptorA,
        HarnessDocument<VideoDecryptorViewModel> decryptorB,
        HarnessDocument<SecretVideoPlayerViewModel> playerA,
        HarnessDocument<SecretVideoPlayerViewModel> playerB,
        HarnessDocument<SecretVideoLibraryViewModel> libraryA,
        HarnessDocument<SecretVideoLibraryViewModel> libraryB)
    {
        EncryptorA = encryptorA.Model;
        EncryptorB = encryptorB.Model;
        DecryptorA = decryptorA.Model;
        DecryptorB = decryptorB.Model;
        PlayerA = playerA.Model;
        PlayerB = playerB.Model;
        LibraryA = libraryA.Model;
        LibraryB = libraryB.Model;
        All =
        [
            encryptorA.Dockable,
            encryptorB.Dockable,
            decryptorA.Dockable,
            decryptorB.Dockable,
            playerA.Dockable,
            playerB.Dockable,
            libraryA.Dockable,
            libraryB.Dockable,
        ];
        _dockables = All.ToDictionary(item => item.Model);
    }

    public VideoEncryptorViewModel EncryptorA { get; }
    public VideoEncryptorViewModel EncryptorB { get; }
    public VideoDecryptorViewModel DecryptorA { get; }
    public VideoDecryptorViewModel DecryptorB { get; }
    public SecretVideoPlayerViewModel PlayerA { get; }
    public SecretVideoPlayerViewModel PlayerB { get; }
    public SecretVideoLibraryViewModel LibraryA { get; }
    public SecretVideoLibraryViewModel LibraryB { get; }
    public IReadOnlyList<ManagedDocumentDockable> All { get; }

    public ManagedDocumentDockable DockableFor(object model) =>
        _dockables.TryGetValue(model, out var dockable)
            ? dockable
            : throw new InvalidOperationException("G8-DOCUMENT-ADAPTER-MISSING");
}

internal sealed record G8P1Report(
    int SchemaVersion,
    string Kind,
    bool Success,
    string Platform,
    string RuntimeVersion,
    int QueueItems,
    int LibraryItems,
    int DocumentCount,
    int FullscreenCycles,
    int DockSwitches,
    int MaximumVisibleContainers,
    long MaximumUiHeartbeatGapMs,
    PlaybackResourceSnapshot FinalResources,
    IReadOnlyDictionary<string, long> StageDurationsMs,
    long ElapsedMs,
    IReadOnlyList<string> FailedScenarioCodes);
