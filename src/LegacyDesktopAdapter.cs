using System;
using System.IO;
using KnowledgeClipper.Desktop.Application;
using KnowledgeClipper.Desktop.Core;

namespace KnowledgeClipper
{
    // Transitional adapter only. The v3 Application layer must not reference any of
    // these file, pipe, authority-discovery, or legacy UI host implementations.
    internal static class LegacyDesktopAdapter
    {
        internal static BoundClipperDependencies CreateBoundClipperDependencies(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return CreateBoundClipperDependencies(config, CreateController(config), true);
        }

        internal static BoundClipperDependencies CreateBoundClipperDependencies(AppConfig config, DesktopController controller, bool initializeModes)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            var jobsRoot = Path.Combine(config.WorkDirectory, "jobs");
            return new BoundClipperDependencies(
                config,
                controller,
                UserSettings.Load(config),
                current => new SettingsForm(CreateSettingsDependencies(config, current, null, null, null, null)),
                new FileQuestionAnswerArtifactReader(jobsRoot),
                new FileOcrHumanResolutionArtifactReader(jobsRoot),
                initializeModes);
        }

        internal static SettingsFormDependencies CreateSettingsDependencies(
            AppConfig config,
            UserSettings current,
            DesktopModuleCapabilityHost injectedCapabilityHost,
            DesktopAnkiProbeHost injectedAnkiProbeHost,
            DesktopModuleActionHost injectedModuleActionHost,
            DesktopContentSettingsHost injectedContentSettingsHost)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var initial = (current ?? UserSettings.Defaults()).Copy();
            var settingsService = new DesktopWorkflowSettingsService(config.WorkDirectory, AppDomain.CurrentDomain.BaseDirectory);
            var moduleSettingsStore = new DesktopModuleSettingsStore(config.WorkDirectory);
            var contentSettingsHost = injectedContentSettingsHost ?? DesktopContentSettingsHost.Load(settingsService, moduleSettingsStore, AppDomain.CurrentDomain.BaseDirectory);
            var capabilityHost = injectedCapabilityHost ?? DesktopModuleCapabilityHost.Load(settingsService.AuthorityReleaseRoot);
            ContentSettingsDescriptorView contentDescriptor = null;
            SpeechSettingsDescriptorView speechDescriptor = null;
            try { contentDescriptor = contentSettingsHost.Describe(); } catch { }
            try { speechDescriptor = SpeechSettingsDescriptorView.Load(AppDomain.CurrentDomain.BaseDirectory); } catch { }
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
                injectedModuleActionHost ?? DesktopModuleActionHost.Load(AppDomain.CurrentDomain.BaseDirectory),
                moduleSettingsStore.Load(),
                SettingsDraft.Open(settingsService.LoadOrCreate(initial), settingsService.CurrentRevision),
                new ProcessAudio(config.FfplayPath));
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
            bool initializeModes)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Controller = controller ?? throw new ArgumentNullException(nameof(controller));
            InitialSettings = (initialSettings ?? throw new ArgumentNullException(nameof(initialSettings))).Copy();
            SettingsFormFactory = settingsFormFactory ?? throw new ArgumentNullException(nameof(settingsFormFactory));
            QuestionAnswers = questionAnswers ?? throw new ArgumentNullException(nameof(questionAnswers));
            OcrHumanResolution = ocrHumanResolution ?? throw new ArgumentNullException(nameof(ocrHumanResolution));
            InitializeModes = initializeModes;
        }

        internal AppConfig Config { get; }
        internal DesktopController Controller { get; }
        internal UserSettings InitialSettings { get; }
        internal Func<UserSettings, SettingsForm> SettingsFormFactory { get; }
        internal IQuestionAnswerArtifactReader QuestionAnswers { get; }
        internal IOcrHumanResolutionArtifactReader OcrHumanResolution { get; }
        internal bool InitializeModes { get; }
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
            ProcessAudio previewAudio)
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
    }
}
