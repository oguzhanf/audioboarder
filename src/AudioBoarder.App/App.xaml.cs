using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Health;
using AudioBoarder.App.HealthCheck;
using AudioBoarder.App.Sessions;
using AudioBoarder.App.Updates;
using AudioBoarder.App.ViewModels;
using AudioBoarder.Core.Rendering;
using AudioBoarder.Core.Scene;
using AudioBoarder.Services;
using AudioBoarder.Services.Imaging;
using AudioBoarder.Services.LLM;
using AudioBoarder.Services.Transcription;
using AudioBoarder.Services.Transcription.Cloud;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Wpf.Ui.Appearance;

namespace AudioBoarder.App;

public partial class App : Application
{
    public IHost? Host { get; private set; }
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioBoarder", "logs");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplySystemTheme();
        ConfigureSerilog();
        WireCrashHandlers();

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            var hostForHealth = BuildHost(e.Args);
            await hostForHealth.StartAsync();
            var exitCode = await HealthCheckCommand.RunAsync(e.Args.Skip(1).ToArray(), hostForHealth.Services);
            await hostForHealth.StopAsync();
            hostForHealth.Dispose();
            Shutdown(exitCode);
            return;
        }

        Host = BuildHost(e.Args);
        await Host.StartAsync();

        var window = Host.Services.GetRequiredService<MainWindow>();
        if (!Onboarding.FirstRunExperience.IsComplete)
            Onboarding.FirstRunExperience.Show(owner: null, markComplete: true);

        MainWindow = window;
        window.Show();
        HandleUpdateResult(e.Args, Host.Services);
        _ = RunStartupSequenceAsync(window, Host.Services);
    }

    private static async Task RunStartupSequenceAsync(Window owner, IServiceProvider services)
    {
        await RunStartupTasksAsync(services);
        await CheckForUpdatesAsync(owner, services);
    }

    private static async Task CheckForUpdatesAsync(Window owner, IServiceProvider services)
    {
        try
        {
            var updateService = services.GetRequiredService<GitHubUpdateService>();
            var release = await updateService.CheckAsync();
            if (release is null || !owner.IsVisible)
                return;

            await owner.Dispatcher.InvokeAsync(() =>
            {
                var viewModel = services.GetRequiredService<MainViewModel>();
                var updateWindow = new UpdateWindow(updateService, release, viewModel) { Owner = owner };
                updateWindow.Show();
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GitHub update check failed");
        }
    }

    private static void HandleUpdateResult(string[] args, IServiceProvider services)
    {
        var failure = args.FirstOrDefault(arg =>
            arg.StartsWith("--update-failed=", StringComparison.OrdinalIgnoreCase));
        if (failure is null)
            return;

        var tag = args.FirstOrDefault(arg =>
            arg.StartsWith("--update-tag=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1] ?? "unknown";
        var code = failure.Split('=', 2)[1];
        services.GetRequiredService<GitHubUpdateService>().RecordFailure(tag);
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBoarder", "updates", tag, "install.log");
        MessageBox.Show(
            $"The {tag} update could not be installed (Windows Installer code {code}). " +
            $"AudioBoarder has reopened and will retry after 24 hours.\n\nInstaller log: {logPath}",
            "AudioBoarder update",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static async Task RunStartupTasksAsync(IServiceProvider services)
    {
        var health = services.GetRequiredService<StartupHealthService>();
        var sessions = services.GetRequiredService<SessionStore>();
        var settings = services.GetRequiredService<IOptions<AudioBoarderSettings>>().Value;
        var vm = services.GetRequiredService<MainViewModel>();
        var creds = services.GetRequiredService<Auth.AzureCredentialProvider>();

        // Try to silently restore a prior sign-in before the first health probe so
        // the Azure pill comes up green for users who already signed in once.
        try
        {
            Log.Information("Probing for cached Azure sign-in token…");
            using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var restored = await creds.TryRestoreAsync(restoreCts.Token);
            Log.Information("Silent token restore returned {Restored}", restored);
        }
        catch (Exception ex) { Log.Warning(ex, "Silent restore threw"); }

        // Hand the signed-in credential chain to the Speech SDK options so it
        // reuses the same MSAL cache as everything else (no extra browser prompt).
        try
        {
            var speechOpts = services.GetRequiredService<IOptions<AzureSpeechSettings>>().Value;
            speechOpts.Credential = creds.Get();
        }
        catch (Exception ex) { Log.Warning(ex, "Could not seed AzureSpeechSettings.Credential"); }

        // Fire health probes immediately in parallel so the UI pills update
        // even if the user is still deciding on the session-restore prompt.
        var healthTask = Task.Run(async () =>
        {
            try { await health.RunAllAsync(); }
            catch (Exception ex) { Log.Error(ex, "Startup health probes failed"); }
        });

        if (settings.Sessions.OfferRestoreOnLaunch)
        {
            try
            {
                var prior = await sessions.LoadLatestAsync();
                if (prior is not null && (prior.Nodes.Length > 0 || prior.Notes.Length > 0))
                {
                    var result = MessageBox.Show(
                        $"A previous session from {prior.SavedAt.LocalDateTime:g} was found with {prior.Nodes.Length} nodes. Restore it?",
                        "AudioBoarder", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        vm.RestoreSession(prior);
                    }
                    else
                    {
                        await sessions.ClearAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Session restore prompt failed");
            }
        }

        await healthTask;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (Host is not null)
        {
            try { await Host.StopAsync(TimeSpan.FromSeconds(3)); }
            catch { /* ignore */ }
            Host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    public static IHost BuildHost(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        var baseDir = AppContext.BaseDirectory;
        builder.Configuration
            .SetBasePath(baseDir)
            .AddJsonFile(Path.Combine(baseDir, "appsettings.json"), optional: true)
            // Personal, machine-specific settings (tenant/subscription/endpoints).
            // Git-ignored so real tenant identifiers never land in source control.
            .AddJsonFile(Path.Combine(baseDir, "appsettings.Local.json"), optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "AUDIOBOARDER_");

        var settingsSection = builder.Configuration.GetSection("AudioBoarder");
        builder.Services.Configure<AudioBoarderSettings>(settingsSection);
        var settings = settingsSection.Get<AudioBoarderSettings>() ?? new AudioBoarderSettings();

        builder.Services.AddOptions<AzureOpenAIOptions>().Configure<IOptions<AudioBoarderSettings>>((opts, root) =>
        {
            var az = root.Value.AzureOpenAI;
            opts.Endpoint = az.Endpoint;
            opts.DeploymentName = az.DeploymentName;
            opts.FallbackDeploymentName = az.FallbackDeploymentName;
            opts.TenantId = az.TenantId;
            opts.ApiKey = az.ApiKey;
            opts.UseManagedIdentity = az.UseManagedIdentity;
            opts.Temperature = az.Temperature;
            if (az.MaxOutputTokens.HasValue) opts.MaxOutputTokens = az.MaxOutputTokens;
        });

        builder.Services.AddOptions<ImageGeneratorOptions>().Configure<IOptions<AudioBoarderSettings>>((opts, root) =>
        {
            var az = root.Value.AzureOpenAI;
            var img = root.Value.ImageGeneration;
            opts.Endpoint = az.Endpoint;
            opts.TenantId = az.TenantId;
            opts.ApiKey = az.ApiKey;
            opts.UseManagedIdentity = az.UseManagedIdentity;
            opts.DeploymentName = img.DeploymentName;
            opts.OpenAIApiVersion = img.OpenAIApiVersion;
            opts.RequestTimeout = img.RequestTimeout;
        });

        builder.Services.AddOptions<CloudTranscriptionOptions>().Configure<IOptions<AudioBoarderSettings>>((opts, root) =>
        {
            var az = root.Value.AzureOpenAI;
            var ct = root.Value.CloudTranscription;
            opts.Endpoint = az.Endpoint;
            opts.TenantId = az.TenantId;
            opts.ApiKey = az.ApiKey;
            opts.UseManagedIdentity = az.UseManagedIdentity;
            opts.DeploymentName = ct.DeploymentName;
            opts.Language = ct.Language;
            opts.OpenAIApiVersion = ct.OpenAIApiVersion;
            opts.WindowSeconds = ct.WindowSeconds;
            opts.Backend = ct.Backend;
            opts.SilenceFlushMs = ct.SilenceFlushMs;
            // Only override the built-in vocabulary prompt when the user actually
            // supplied one. A blank value in appsettings must not silently disable
            // domain biasing — that is what made product names come back wrong.
            if (ct.Prompt is not null)
                opts.Prompt = ct.Prompt;
            opts.Temperature = ct.Temperature;
        });

        builder.Services.AddOptions<AudioBoarder.Services.Audio.AudioCaptureOptions>().Configure<IOptions<AudioBoarderSettings>>((opts, root) =>
        {
            var a = root.Value.Audio;
            opts.CaptureMicrophone = a.CaptureMicrophone;
            opts.CaptureLoopback = a.CaptureLoopback;
            opts.SileroModelPath = a.SileroModelPath;
        });

        builder.Services.AddOptions<AzureSpeechSettings>().Configure<IOptions<AudioBoarderSettings>>((opts, root) =>
        {
            var sp = root.Value.AzureSpeech;
            var ak = Environment.GetEnvironmentVariable("AUDIOBOARDER_SPEECH_KEY");
            var ar = Environment.GetEnvironmentVariable("AUDIOBOARDER_SPEECH_REGION");
            opts.Region = !string.IsNullOrWhiteSpace(ar) ? ar : sp.Region;
            opts.ResourceId = sp.ResourceId;
            opts.ApiKey = !string.IsNullOrWhiteSpace(ak) ? ak : sp.ApiKey;
            opts.TenantId = root.Value.AzureOpenAI.TenantId;
            opts.Language = sp.Language;
            opts.EndSilenceMs = sp.EndSilenceMs;
        });

        builder.Services.AddSingleton(sp =>
        {
            var w = sp.GetRequiredService<IOptions<AudioBoarderSettings>>().Value.Whisper;
            return new WhisperOptions(w.ModelSize, w.ModelPath, w.Language, w.WindowSeconds, w.AutoDownload);
        });

        builder.Services.AddAudioBoarder();

        // Registered before AddAudioBoarder's orchestrator resolves it, so the board's
        // size cap is configurable rather than hard-coded.
        builder.Services.AddSingleton(_ => new SceneBudget(
            settings.Realtime.MaxNodes, settings.Realtime.MaxNotes));

        builder.Services.AddSingleton<DiagramTheme>(_ =>
            string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? DiagramTheme.Dark : DiagramTheme.Light);

        builder.Services.AddSingleton<Auth.AzureCredentialProvider>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<Export.DiagramExporter>();
        builder.Services.AddSingleton<Export.ExcalidrawExporter>();
        builder.Services.AddSingleton<StartupHealthService>();
        builder.Services.AddSingleton<Continuous.ContinuousDiagrammer>();
        builder.Services.AddSingleton(sp => new GitHubUpdateService(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<ILogger<GitHubUpdateService>>()));

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);
        return builder.Build();
    }

    private static void ConfigureSerilog()
    {
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, "audioboarder-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5_000_000,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console()
            .CreateLogger();
        Log.Information("AudioBoarder starting; logs at {Path}", LogDirectory);
    }

    private void WireCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled AppDomain exception (terminating={Terminating})", e.IsTerminating);
        };
        DispatcherUnhandledException += (s, e) =>
        {
            Log.Error(e.Exception, "Unhandled dispatcher exception");
            MessageBox.Show(
                $"AudioBoarder encountered an unexpected error and will continue if possible.\n\n{e.Exception.Message}\n\nDetails were written to:\n{LogDirectory}",
                "AudioBoarder", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Warning(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    private static void ApplySystemTheme()
    {
        var theme = SystemThemeManager.GetCachedSystemTheme() switch
        {
            SystemTheme.Dark => ApplicationTheme.Dark,
            SystemTheme.HCWhite or SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2
                => ApplicationTheme.HighContrast,
            _ => ApplicationTheme.Light,
        };
        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
    }
}
