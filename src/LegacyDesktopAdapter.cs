using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KnowledgeClipper.Desktop.Application;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    // Transitional adapter only. The v3 Application layer must not reference any of
    // these file, pipe, authority-discovery, or legacy UI host implementations.
    internal static class LegacyDesktopAdapter
    {
        internal static BoundClipperDependencies CreateBoundClipperDependencies(AppConfig config, string captureFixturePath = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return CreateBoundClipperDependencies(config, CreateController(config), true, CreateReadToAnkiApplication(config, captureFixturePath));
        }

        internal static BoundClipperDependencies CreateBoundClipperDependencies(AppConfig config, DesktopController controller, bool initializeModes, DesktopCompositionRoot application = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            var jobsRoot = Path.Combine(config.WorkDirectory, "jobs");
            return new BoundClipperDependencies(
                config,
                controller,
                UserSettings.Load(config),
                current => new SettingsForm(CreateSettingsDependencies(config, current, null, null, null, null, application)),
                new FileQuestionAnswerArtifactReader(jobsRoot),
                new FileOcrHumanResolutionArtifactReader(jobsRoot),
                initializeModes,
                application);
        }

        internal static SettingsFormDependencies CreateSettingsDependencies(
            AppConfig config,
            UserSettings current,
            DesktopModuleCapabilityHost injectedCapabilityHost,
            DesktopAnkiProbeHost injectedAnkiProbeHost,
            DesktopModuleActionHost injectedModuleActionHost,
            DesktopContentSettingsHost injectedContentSettingsHost,
            DesktopCompositionRoot application = null,
            string applicationBaseDirectory = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var initial = (current ?? UserSettings.Defaults()).Copy();
            var baseDirectory = applicationBaseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            var settingsService = new DesktopWorkflowSettingsService(config.WorkDirectory, baseDirectory);
            var moduleSettingsStore = new DesktopModuleSettingsStore(config.WorkDirectory);
            var contentSettingsHost = injectedContentSettingsHost ?? DesktopContentSettingsHost.Load(settingsService, moduleSettingsStore, baseDirectory);
            var capabilityHost = injectedCapabilityHost ?? DesktopModuleCapabilityHost.Load(settingsService.AuthorityReleaseRoot);
            ContentSettingsDescriptorView contentDescriptor = null;
            SpeechSettingsDescriptorView speechDescriptor = null;
            try { contentDescriptor = contentSettingsHost.Describe(); } catch { }
            try { speechDescriptor = SpeechSettingsDescriptorView.Load(baseDirectory); } catch { }
            return new SettingsFormDependencies(
                config,
                initial,
                DesktopCredentialSettings.Load(config.WorkDirectory),
                settingsService,
                moduleSettingsStore,
                new DesktopSpeechCatalogCacheStore(config.WorkDirectory),
                contentSettingsHost,
                capabilityHost,
                contentDescriptor,
                speechDescriptor,
                injectedAnkiProbeHost ?? DesktopAnkiProbeHost.Load(settingsService, moduleSettingsStore),
                injectedModuleActionHost ?? DesktopModuleActionHost.Load(baseDirectory),
                moduleSettingsStore.Load(),
                SettingsDraft.Open(settingsService.LoadOrCreate(initial), settingsService.CurrentRevision),
                new ProcessAudio(config.FfplayPath),
                application ?? CreateReadToAnkiApplication(config, null));
        }

        internal static string ResolveProductionPipeName(string baseDirectory)
        {
            var integration = CandidateIntegrationConfig.Load(baseDirectory);
            return integration == null ? NamedPipeCommonIpcClient.DefaultPipeName : integration.PipeName;
        }

        private static DesktopController CreateController(AppConfig config)
        {
            var clock = new SystemDesktopClock();
            var jobs = Path.Combine(config.WorkDirectory, "jobs");
            var settings = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
            settings.LoadOrCreate(UserSettings.Load(config));
            var commands = new FileDesktopCommonCommandSource(jobs, clock, new SystemCommonIdProvider(), new CoreDesktopTaskSettingsPublisher(settings));
            var pipe = new NamedPipeCommonIpcClient(ResolveProductionPipeName(AppDomain.CurrentDomain.BaseDirectory));
            return new DesktopController(
                new WindowsDesktopCaptureProvider(config.WorkDirectory),
                new WindowsScreenshotProvider(config.WorkDirectory),
                new NamedPipeDesktopOrchestratorPort(pipe, commands),
                clock,
                new SystemDesktopIdProvider());
        }

        private static DesktopCompositionRoot CreateReadToAnkiApplication(AppConfig config, string captureFixturePath)
        {
            var settings = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
            settings.LoadOrCreate(UserSettings.Load(config));
            var moduleSettings = new DesktopModuleSettingsStore(config.WorkDirectory);
            var destinations = new DesktopDestinationSettingsPublisher(
                () => settings.LoadOrCreate(UserSettings.Load(config)), moduleSettings.Load,
                draft => DesktopTaskSettingsArtifacts.BuildAnkiProbeConfigurationJson(settings, draft));
            var gateway = destinations.CreateGateway(
                ResolveProductionPipeName(AppDomain.CurrentDomain.BaseDirectory),
                () =>
                {
                    var current = moduleSettings.Load();
                    return new SdkLanguageCardSettings("desktop-module-settings.v1", current.ContentLanguagePracticeDefault);
                });
            var local = new ReadToAnkiOnlyApplicationSupportPorts();
            Func<ContextModeState> readCaptureMode = () =>
            {
                var document = settings.LoadOrCreate(UserSettings.Load(config));
                var capture = document.Profiles[document.ActiveProfileId].Capture;
                return new ContextModeState { enabled = capture?.ContextEnabled == true, granularity = capture?.ContextGranularity ?? "sentence" };
            };
            Action<string> traceCapture = phase =>
            {
                try
                {
                    var logs = Path.Combine(config.WorkDirectory, "logs"); Directory.CreateDirectory(logs);
                    File.AppendAllText(Path.Combine(logs, "desktop-ui.log"), DateTime.Now.ToString("o") + " READ_CAPTURE phase=" + phase + Environment.NewLine);
                }
                catch { }
            };
            ICurrentTextCapturePort capturePort = captureFixturePath == null
                ? new WindowsCurrentTextCapturePort(new SystemWindowsClipboard(), new SystemCopyShortcutSender(), 25, 40, new WindowsUiAutomationContextProvider(), readCaptureMode, traceCapture)
                : new CandidateCaptureFixture(captureFixturePath, readCaptureMode, traceCapture);
            return new DesktopCompositionRoot(
                gateway, gateway, gateway, gateway, gateway,
                local, local, local,
                null,
                capturePort,
                gateway, gateway, null, gateway);
        }
    }

    internal sealed class ReadToAnkiOnlyApplicationSupportPorts : ISettingsRepository, ICredentialStore, IWindowActivationPort
    {
        public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsSnapshot("legacy-settings-host", "read-only", new Dictionary<string, object>()));

        public Task<SettingsSnapshot> SaveAsync(SettingsSnapshot settings, CancellationToken cancellationToken) =>
            throw new NotSupportedException("desktop-v3-settings-migration-unavailable");

        public Task<CredentialMetadata> DescribeAsync(string credentialId, CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialMetadata(credentialId, false, false, "legacy-settings-host"));

        public Task StoreAsync(string credentialId, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException("desktop-v3-credential-migration-unavailable");

        public Task<WindowActivationSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WindowActivationSnapshot(0, "read-to-anki", null, 0));

        public Task<bool> RestoreAsync(long nativeWindowId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    internal sealed class BoundClipperDependencies
    {
        internal BoundClipperDependencies(
            AppConfig config,
            DesktopController controller,
            UserSettings initialSettings,
            Func<UserSettings, SettingsForm> settingsFormFactory,
            IQuestionAnswerArtifactReader questionAnswers,
            IOcrHumanResolutionArtifactReader ocrHumanResolution,
            bool initializeModes,
            DesktopCompositionRoot application = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Controller = controller ?? throw new ArgumentNullException(nameof(controller));
            InitialSettings = (initialSettings ?? throw new ArgumentNullException(nameof(initialSettings))).Copy();
            SettingsFormFactory = settingsFormFactory ?? throw new ArgumentNullException(nameof(settingsFormFactory));
            QuestionAnswers = questionAnswers ?? throw new ArgumentNullException(nameof(questionAnswers));
            OcrHumanResolution = ocrHumanResolution ?? throw new ArgumentNullException(nameof(ocrHumanResolution));
            InitializeModes = initializeModes;
            Application = application;
        }

        internal AppConfig Config { get; }
        internal DesktopController Controller { get; }
        internal UserSettings InitialSettings { get; }
        internal Func<UserSettings, SettingsForm> SettingsFormFactory { get; }
        internal IQuestionAnswerArtifactReader QuestionAnswers { get; }
        internal IOcrHumanResolutionArtifactReader OcrHumanResolution { get; }
        internal bool InitializeModes { get; }
        internal DesktopCompositionRoot Application { get; }
    }

    internal sealed class SettingsFormDependencies
    {
        internal SettingsFormDependencies(
            AppConfig config,
            UserSettings initial,
            DesktopCredentialSettings initialCredentials,
            DesktopWorkflowSettingsService settingsService,
            DesktopModuleSettingsStore moduleSettingsStore,
            DesktopSpeechCatalogCacheStore speechCatalogCacheStore,
            DesktopContentSettingsHost contentSettingsHost,
            DesktopModuleCapabilityHost capabilityHost,
            ContentSettingsDescriptorView contentDescriptor,
            SpeechSettingsDescriptorView speechDescriptor,
            DesktopAnkiProbeHost ankiProbeHost,
            DesktopModuleActionHost moduleActionHost,
            DesktopModuleSettingsDocument moduleSettings,
            SettingsDraft settingsDraft,
            ProcessAudio previewAudio,
            DesktopCompositionRoot application = null)
        {
            Config = config;
            Initial = initial;
            InitialCredentials = initialCredentials;
            SettingsService = settingsService;
            ModuleSettingsStore = moduleSettingsStore;
            SpeechCatalogCacheStore = speechCatalogCacheStore;
            ContentSettingsHost = contentSettingsHost;
            CapabilityHost = capabilityHost;
            ContentDescriptor = contentDescriptor;
            SpeechDescriptor = speechDescriptor;
            AnkiProbeHost = ankiProbeHost;
            ModuleActionHost = moduleActionHost;
            ModuleSettings = moduleSettings;
            SettingsDraft = settingsDraft;
            PreviewAudio = previewAudio;
            Application = application;
        }

        internal AppConfig Config { get; }
        internal UserSettings Initial { get; }
        internal DesktopCredentialSettings InitialCredentials { get; }
        internal DesktopWorkflowSettingsService SettingsService { get; }
        internal DesktopModuleSettingsStore ModuleSettingsStore { get; }
        internal DesktopSpeechCatalogCacheStore SpeechCatalogCacheStore { get; }
        internal DesktopContentSettingsHost ContentSettingsHost { get; }
        internal DesktopModuleCapabilityHost CapabilityHost { get; }
        internal ContentSettingsDescriptorView ContentDescriptor { get; }
        internal SpeechSettingsDescriptorView SpeechDescriptor { get; }
        internal DesktopAnkiProbeHost AnkiProbeHost { get; }
        internal DesktopModuleActionHost ModuleActionHost { get; }
        internal DesktopModuleSettingsDocument ModuleSettings { get; }
        internal SettingsDraft SettingsDraft { get; }
        internal ProcessAudio PreviewAudio { get; }
        internal DesktopCompositionRoot Application { get; }
    }
}
