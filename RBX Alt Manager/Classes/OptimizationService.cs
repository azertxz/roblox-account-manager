using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RBX_Alt_Manager.Classes
{
    internal enum OptimizationBackend
    {
        Unknown,
        NativeRoblox,
        Bloxstrap,
        Fishstrap
    }

    internal enum OptimizationBackendPreference
    {
        Auto,
        NativeRoblox,
        Bloxstrap,
        Fishstrap
    }

    internal enum OptimizationProfile
    {
        Balanced,
        Performance,
        LowEnd,
        Custom
    }

    internal enum OptimizationResultState
    {
        Applied,
        Skipped,
        Unsupported,
        Failed
    }

    internal sealed class OptimizationCapabilityStatus
    {
        public string Name { get; }
        public bool Supported { get; }

        public OptimizationCapabilityStatus(string name, bool supported)
        {
            Name = name;
            Supported = supported;
        }
    }

    internal sealed class OptimizationCapabilities
    {
        public bool SupportsCustomClientSettings { get; }
        public bool SupportsFpsCap { get; }
        public bool SupportsAdvancedFlags { get; }
        public bool SupportsRuntimeTuning { get; }

        public OptimizationCapabilities(bool supportsCustomClientSettings, bool supportsFpsCap, bool supportsAdvancedFlags, bool supportsRuntimeTuning)
        {
            SupportsCustomClientSettings = supportsCustomClientSettings;
            SupportsFpsCap = supportsFpsCap;
            SupportsAdvancedFlags = supportsAdvancedFlags;
            SupportsRuntimeTuning = supportsRuntimeTuning;
        }

        public List<OptimizationCapabilityStatus> ToStatusList()
        {
            return new List<OptimizationCapabilityStatus>
            {
                new OptimizationCapabilityStatus("CustomClientSettings", SupportsCustomClientSettings),
                new OptimizationCapabilityStatus("FpsCap", SupportsFpsCap),
                new OptimizationCapabilityStatus("AdvancedFlags", SupportsAdvancedFlags),
                new OptimizationCapabilityStatus("RuntimeTuning", SupportsRuntimeTuning)
            };
        }
    }

    internal sealed class OptimizationProviderInfo
    {
        public OptimizationBackend Backend { get; }
        public string DisplayName { get; }
        public OptimizationCapabilities Capabilities { get; }
        public OptimizationBackendPreference Preference { get; }
        public string SelectionReason { get; }

        public OptimizationProviderInfo(OptimizationBackend backend, string displayName, OptimizationCapabilities capabilities, OptimizationBackendPreference preference, string selectionReason)
        {
            Backend = backend;
            DisplayName = displayName;
            Capabilities = capabilities;
            Preference = preference;
            SelectionReason = selectionReason;
        }
    }

    internal sealed class OptimizationSettingResult
    {
        public string Name { get; }
        public OptimizationResultState State { get; }
        public string Detail { get; }

        public OptimizationSettingResult(string name, OptimizationResultState state, string detail)
        {
            Name = name;
            State = state;
            Detail = detail;
        }
    }

    internal sealed class OptimizationApplyReport
    {
        public bool Success { get; set; }
        public string Reason { get; set; }
        public string SettingsFilePath { get; set; }
        public string BackupFilePath { get; set; }
        public OptimizationBackend Backend { get; set; }
        public string BackendDisplayName { get; set; }
        public List<OptimizationSettingResult> Results { get; } = new List<OptimizationSettingResult>();
    }

    internal interface IOptimizationProvider
    {
        OptimizationBackend Backend { get; }
        string DisplayName { get; }
        OptimizationCapabilities Capabilities { get; }
        bool IsAvailable();
        bool TryResolveClientSettingsPath(out string settingsFilePath, out string reason);
    }

    internal static class OptimizationService
    {
        private const string BackupSuffix = ".ramopt.bak";

        private static readonly object InitLock = new object();
        private static readonly object ApplyLock = new object();
        private static readonly List<IOptimizationProvider> Providers = new List<IOptimizationProvider>
        {
            new FishstrapOptimizationProvider(),
            new BloxstrapOptimizationProvider(),
            new NativeRobloxOptimizationProvider()
        };

        private static OptimizationProviderInfo ActiveProviderInfo;

        private sealed class OptimizationSettingDefinition
        {
            public string Name { get; }
            public JToken Value { get; }
            public bool RequiresAdvancedFlags { get; }

            public OptimizationSettingDefinition(string name, JToken value, bool requiresAdvancedFlags)
            {
                Name = name;
                Value = value;
                RequiresAdvancedFlags = requiresAdvancedFlags;
            }
        }

        public static OptimizationProviderInfo Initialize()
        {
            lock (InitLock)
            {
                OptimizationBackendPreference preference = GetBackendPreference();
                IOptimizationProvider selected = ResolveProvider(preference, out string reason);

                if (selected == null)
                {
                    ActiveProviderInfo = new OptimizationProviderInfo(OptimizationBackend.Unknown, "Unavailable", new OptimizationCapabilities(false, false, false, false), preference, "No optimization provider could be detected.");
                    UpdateDetectedSettings(ActiveProviderInfo);
                    Program.Logger.Error("Optimization backend detection failed: no provider available.");
                    return ActiveProviderInfo;
                }

                ActiveProviderInfo = new OptimizationProviderInfo(selected.Backend, selected.DisplayName, selected.Capabilities, preference, reason);
                UpdateDetectedSettings(ActiveProviderInfo);
                Program.Logger.Info($"Optimization backend selected: {ActiveProviderInfo.DisplayName} ({ActiveProviderInfo.Backend}) | Preference={preference} | {reason}");
                return ActiveProviderInfo;
            }
        }

        public static OptimizationProviderInfo GetProviderInfo()
        {
            return ActiveProviderInfo ?? Initialize();
        }

        public static bool TryGetClientSettingsPath(out string settingsFilePath, out string reason)
        {
            settingsFilePath = string.Empty;
            reason = string.Empty;

            OptimizationProviderInfo info = GetProviderInfo();

            IOptimizationProvider activeProvider = Providers.FirstOrDefault(x => x.Backend == info.Backend);

            if (activeProvider != null && activeProvider.TryResolveClientSettingsPath(out settingsFilePath, out reason))
                return true;

            foreach (IOptimizationProvider provider in Providers)
            {
                if (provider.Backend == info.Backend)
                    continue;

                if (!provider.IsAvailable())
                    continue;

                if (provider.TryResolveClientSettingsPath(out settingsFilePath, out reason))
                {
                    Program.Logger.Info($"Optimization path fallback used provider {provider.DisplayName}: {reason}");
                    return true;
                }
            }

            reason = string.IsNullOrEmpty(reason) ? "No provider could resolve a ClientAppSettings path." : reason;
            return false;
        }

        public static OptimizationApplyReport ApplyLaunchSettings()
        {
            lock (ApplyLock)
            {
                Initialize();

                if (AccountManager.General.Exists("CustomClientSettings") && File.Exists(AccountManager.General.Get<string>("CustomClientSettings")))
                    return ApplyCustomClientSettings();

                if (AccountManager.General.Get<bool>("EnableAutoOptimization"))
                    return ApplyConfiguredProfile("launch");

                return ApplyLegacyUnlockFps();
            }
        }

        public static OptimizationApplyReport ApplyConfiguredProfile(string trigger)
        {
            lock (ApplyLock)
            {
                OptimizationApplyReport report = new OptimizationApplyReport();

                if (!AccountManager.General.Get<bool>("EnableAutoOptimization"))
                {
                    report.Success = true;
                    report.Reason = "Auto optimization is disabled.";
                    report.Results.Add(new OptimizationSettingResult("AutoOptimization", OptimizationResultState.Skipped, "Enable Auto Optimization to apply a profile."));
                    return report;
                }

                if (!TryGetClientSettingsPath(out string settingsFilePath, out string resolveReason))
                {
                    report.Success = false;
                    report.Reason = resolveReason;
                    return report;
                }

                report.SettingsFilePath = settingsFilePath;
                report.Backend = GetProviderInfo().Backend;
                report.BackendDisplayName = GetProviderInfo().DisplayName;

                DirectoryInfo settingsFolder = new FileInfo(settingsFilePath).Directory;

                if (settingsFolder == null)
                {
                    report.Success = false;
                    report.Reason = $"Invalid settings path: {settingsFilePath}";
                    return report;
                }

                if (!settingsFolder.Exists)
                    settingsFolder.Create();

                string backupFilePath = settingsFilePath + BackupSuffix;
                report.BackupFilePath = backupFilePath;

                try
                {
                    EnsureBackup(settingsFilePath, backupFilePath);

                    JObject root = ReadSettingsFile(settingsFilePath);
                    List<OptimizationSettingDefinition> definitions = BuildProfileDefinitions(GetCurrentProfile(), GetProviderInfo().Capabilities);

                    List<OptimizationSettingDefinition> appliedDefinitions = new List<OptimizationSettingDefinition>();

                    foreach (OptimizationSettingDefinition definition in definitions)
                    {
                        if (definition.RequiresAdvancedFlags && !GetProviderInfo().Capabilities.SupportsAdvancedFlags)
                        {
                            report.Results.Add(new OptimizationSettingResult(definition.Name, OptimizationResultState.Unsupported, "Active backend does not support advanced flags."));
                            continue;
                        }

                        root[definition.Name] = definition.Value;
                        appliedDefinitions.Add(definition);
                        report.Results.Add(new OptimizationSettingResult(definition.Name, OptimizationResultState.Applied, $"Applied value {definition.Value}."));
                    }

                    File.WriteAllText(settingsFilePath, root.ToString(Formatting.None));

                    if (!ValidateAppliedSettings(settingsFilePath, appliedDefinitions, out string validationReason))
                    {
                        RestoreFromBackup(settingsFilePath, backupFilePath);
                        report.Success = false;
                        report.Reason = $"Validation failed after apply. {validationReason}. Backup restored.";
                        Program.Logger.Error($"Optimization apply failed validation: {report.Reason}");
                        SaveApplyMetadata(false, report.Reason, trigger, definitions.Select(x => x.Name));
                        return report;
                    }

                    report.Success = true;
                    report.Reason = $"Applied optimization profile {GetCurrentProfile()} via {GetProviderInfo().DisplayName}.";
                    Program.Logger.Info($"Optimization apply success ({trigger}): {report.Reason} Path={settingsFilePath}; Resolver={resolveReason}");
                    SaveApplyMetadata(true, report.Reason, trigger, definitions.Select(x => x.Name));
                    return report;
                }
                catch (Exception ex)
                {
                    try { RestoreFromBackup(settingsFilePath, backupFilePath); } catch { }

                    report.Success = false;
                    report.Reason = $"Apply failed with exception: {ex.Message}";
                    Program.Logger.Error($"Optimization apply exception: {ex}");
                    SaveApplyMetadata(false, report.Reason, trigger, Array.Empty<string>());
                    return report;
                }
            }
        }

        public static OptimizationApplyReport RestoreDefaults()
        {
            lock (ApplyLock)
            {
                OptimizationApplyReport report = new OptimizationApplyReport();

                if (!TryGetClientSettingsPath(out string settingsFilePath, out string resolveReason))
                {
                    report.Success = false;
                    report.Reason = resolveReason;
                    return report;
                }

                report.SettingsFilePath = settingsFilePath;
                report.BackupFilePath = settingsFilePath + BackupSuffix;
                report.Backend = GetProviderInfo().Backend;
                report.BackendDisplayName = GetProviderInfo().DisplayName;

                try
                {
                    if (File.Exists(report.BackupFilePath))
                    {
                        RestoreFromBackup(settingsFilePath, report.BackupFilePath);
                        report.Success = true;
                        report.Reason = "Restored ClientAppSettings from optimization backup.";
                        Program.Logger.Info($"Optimization restore success: {report.Reason} Path={settingsFilePath}");
                        SaveApplyMetadata(true, "Restored from backup", "restore", Array.Empty<string>());
                        return report;
                    }

                    JObject root = ReadSettingsFile(settingsFilePath);

                    string keysRaw = AccountManager.General.Exists("OptimizationLastAppliedKeys") ? AccountManager.General.Get<string>("OptimizationLastAppliedKeys") : "None";
                    IEnumerable<string> keys = keysRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x != "None");

                    foreach (string key in keys)
                    {
                        if (root.ContainsKey(key))
                        {
                            root.Remove(key);
                            report.Results.Add(new OptimizationSettingResult(key, OptimizationResultState.Applied, "Removed key during restore."));
                        }
                    }

                    File.WriteAllText(settingsFilePath, root.ToString(Formatting.None));
                    report.Success = true;
                    report.Reason = "Removed last applied optimization keys (no backup file found).";
                    SaveApplyMetadata(true, "Restored by key removal", "restore", Array.Empty<string>());
                    return report;
                }
                catch (Exception ex)
                {
                    report.Success = false;
                    report.Reason = $"Restore failed: {ex.Message}";
                    Program.Logger.Error($"Optimization restore exception: {ex}");
                    return report;
                }
            }
        }

        public static void ApplyOnStartupIfEnabled()
        {
            if (!AccountManager.General.Get<bool>("EnableAutoOptimization"))
                return;

            if (!AccountManager.General.Get<bool>("OptimizationApplyOnStartup"))
                return;

            ApplyLaunchSettings();
        }

        public static void HandleClientVersionDetected(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return;

            if (!AccountManager.General.Get<bool>("EnableAutoOptimization"))
                return;

            if (!AccountManager.General.Get<bool>("OptimizationReapplyAfterClientUpdate"))
                return;

            string lastAppliedVersion = AccountManager.General.Exists("OptimizationLastAppliedVersion") ? AccountManager.General.Get<string>("OptimizationLastAppliedVersion") : "Unknown";

            if (!string.Equals(lastAppliedVersion, version, StringComparison.OrdinalIgnoreCase))
                ApplyLaunchSettings();
        }

        private static OptimizationApplyReport ApplyCustomClientSettings()
        {
            OptimizationApplyReport report = new OptimizationApplyReport();

            if (!TryGetClientSettingsPath(out string settingsFilePath, out string reason))
            {
                report.Success = false;
                report.Reason = reason;
                return report;
            }

            string customPath = AccountManager.General.Get<string>("CustomClientSettings");

            if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
            {
                report.Success = false;
                report.Reason = "Custom client settings path is missing or invalid.";
                return report;
            }

            DirectoryInfo settingsFolder = new FileInfo(settingsFilePath).Directory;
            if (settingsFolder == null)
            {
                report.Success = false;
                report.Reason = $"Invalid settings path: {settingsFilePath}";
                return report;
            }

            if (!settingsFolder.Exists)
                settingsFolder.Create();

            string backupFilePath = settingsFilePath + BackupSuffix;

            EnsureBackup(settingsFilePath, backupFilePath);
            File.Copy(customPath, settingsFilePath, true);

            report.Success = true;
            report.Reason = "Applied custom client settings file.";
            report.SettingsFilePath = settingsFilePath;
            report.BackupFilePath = backupFilePath;
            report.Backend = GetProviderInfo().Backend;
            report.BackendDisplayName = GetProviderInfo().DisplayName;
            report.Results.Add(new OptimizationSettingResult("CustomClientSettings", OptimizationResultState.Applied, customPath));
            Program.Logger.Info($"Optimization custom settings applied from {customPath} -> {settingsFilePath}");
            SaveApplyMetadata(true, report.Reason, "custom", new[] { "CustomClientSettings" });
            return report;
        }

        private static OptimizationApplyReport ApplyLegacyUnlockFps()
        {
            OptimizationApplyReport report = new OptimizationApplyReport();

            if (!AccountManager.General.Get<bool>("UnlockFPS"))
            {
                report.Success = true;
                report.Reason = "Legacy FPS unlock is disabled and auto optimization is disabled.";
                report.Results.Add(new OptimizationSettingResult("DFIntTaskSchedulerTargetFps", OptimizationResultState.Skipped, "No optimization action required."));
                return report;
            }

            if (!TryGetClientSettingsPath(out string settingsFilePath, out string reason))
            {
                report.Success = false;
                report.Reason = reason;
                return report;
            }

            DirectoryInfo settingsFolder = new FileInfo(settingsFilePath).Directory;
            if (settingsFolder == null)
            {
                report.Success = false;
                report.Reason = $"Invalid settings path: {settingsFilePath}";
                return report;
            }

            if (!settingsFolder.Exists)
                settingsFolder.Create();

            string backupFilePath = settingsFilePath + BackupSuffix;
            EnsureBackup(settingsFilePath, backupFilePath);

            JObject root = ReadSettingsFile(settingsFilePath);
            int fps = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;
            root["DFIntTaskSchedulerTargetFps"] = fps;
            File.WriteAllText(settingsFilePath, root.ToString(Formatting.None));

            if (!ValidateAppliedSettings(settingsFilePath, new[] { new OptimizationSettingDefinition("DFIntTaskSchedulerTargetFps", fps, false) }, out string validationReason))
            {
                RestoreFromBackup(settingsFilePath, backupFilePath);
                report.Success = false;
                report.Reason = $"Legacy FPS apply failed validation: {validationReason}. Backup restored.";
                return report;
            }

            report.Success = true;
            report.Reason = $"Applied legacy FPS unlock ({fps}).";
            report.SettingsFilePath = settingsFilePath;
            report.BackupFilePath = backupFilePath;
            report.Backend = GetProviderInfo().Backend;
            report.BackendDisplayName = GetProviderInfo().DisplayName;
            report.Results.Add(new OptimizationSettingResult("DFIntTaskSchedulerTargetFps", OptimizationResultState.Applied, fps.ToString()));
            SaveApplyMetadata(true, report.Reason, "legacy-fps", new[] { "DFIntTaskSchedulerTargetFps" });
            return report;
        }

        private static OptimizationProfile GetCurrentProfile()
        {
            if (AccountManager.General.Exists("OptimizationProfile"))
            {
                string raw = AccountManager.General.Get<string>("OptimizationProfile")?.Trim();

                if (Enum.TryParse(raw, true, out OptimizationProfile parsed))
                    return parsed;
            }

            return OptimizationProfile.Balanced;
        }

        private static List<OptimizationSettingDefinition> BuildProfileDefinitions(OptimizationProfile profile, OptimizationCapabilities capabilities)
        {
            int fps;

            switch (profile)
            {
                case OptimizationProfile.Performance:
                    fps = 240;
                    break;
                case OptimizationProfile.LowEnd:
                    fps = 60;
                    break;
                case OptimizationProfile.Custom:
                    fps = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 120;
                    break;
                default:
                    fps = 120;
                    break;
            }

            List<OptimizationSettingDefinition> definitions = new List<OptimizationSettingDefinition>
            {
                new OptimizationSettingDefinition("DFIntTaskSchedulerTargetFps", fps, false)
            };

            if (profile == OptimizationProfile.LowEnd || profile == OptimizationProfile.Performance)
            {
                definitions.Add(new OptimizationSettingDefinition("FFlagDebugDisablePostFx", profile == OptimizationProfile.LowEnd, true));
                definitions.Add(new OptimizationSettingDefinition("FFlagDebugDisableShadows", profile == OptimizationProfile.LowEnd, true));
            }

            if (!capabilities.SupportsFpsCap)
                definitions = definitions.Where(x => x.Name != "DFIntTaskSchedulerTargetFps").ToList();

            return definitions;
        }

        private static JObject ReadSettingsFile(string settingsFilePath)
        {
            if (!File.Exists(settingsFilePath))
                return new JObject();

            string content = File.ReadAllText(settingsFilePath);

            if (string.IsNullOrWhiteSpace(content))
                return new JObject();

            if (content.TryParseJson(out JObject parsed))
                return parsed;

            return new JObject();
        }

        private static bool ValidateAppliedSettings(string settingsFilePath, IEnumerable<OptimizationSettingDefinition> definitions, out string reason)
        {
            reason = "";

            if (!File.Exists(settingsFilePath))
            {
                reason = "Settings file does not exist after write";
                return false;
            }

            string content = File.ReadAllText(settingsFilePath);

            if (!content.TryParseJson(out JObject root))
            {
                reason = "Settings file is not valid JSON";
                return false;
            }

            foreach (OptimizationSettingDefinition definition in definitions)
            {
                if (!root.ContainsKey(definition.Name))
                {
                    reason = $"Missing key {definition.Name}";
                    return false;
                }

                if (!JToken.DeepEquals(root[definition.Name], definition.Value))
                {
                    reason = $"Value mismatch for {definition.Name}";
                    return false;
                }
            }

            return true;
        }

        private static void EnsureBackup(string settingsFilePath, string backupFilePath)
        {
            if (File.Exists(backupFilePath))
                return;

            if (File.Exists(settingsFilePath))
                File.Copy(settingsFilePath, backupFilePath, true);
            else
                File.WriteAllText(backupFilePath, "{}");
        }

        private static void RestoreFromBackup(string settingsFilePath, string backupFilePath)
        {
            if (!File.Exists(backupFilePath))
                return;

            File.Copy(backupFilePath, settingsFilePath, true);
        }

        private static void SaveApplyMetadata(bool success, string status, string trigger, IEnumerable<string> keys)
        {
            if (AccountManager.General == null)
                return;

            AccountManager.General.Set("OptimizationLastApplyStatus", success ? "Success" : "Failed");
            AccountManager.General.Set("OptimizationLastApplyReason", status);
            AccountManager.General.Set("OptimizationLastApplyTrigger", trigger);
            AccountManager.General.Set("OptimizationLastApplyUtc", DateTime.UtcNow.ToString("o"));
            AccountManager.General.Set("OptimizationLastAppliedBackend", GetProviderInfo().Backend.ToString());
            AccountManager.General.Set("OptimizationLastAppliedProvider", GetProviderInfo().DisplayName);
            AccountManager.General.Set("OptimizationLastAppliedProfile", GetCurrentProfile().ToString());
            AccountManager.General.Set("OptimizationLastAppliedKeys", string.Join(",", keys.Any() ? keys : new[] { "None" }));

            if (!string.IsNullOrWhiteSpace(AccountManager.CurrentVersion))
                AccountManager.General.Set("OptimizationLastAppliedVersion", AccountManager.CurrentVersion);

            AccountManager.IniSettings?.Save("RAMSettings.ini");
        }

        private static IOptimizationProvider ResolveProvider(OptimizationBackendPreference preference, out string reason)
        {
            reason = "";

            if (preference != OptimizationBackendPreference.Auto)
            {
                OptimizationBackend preferredBackend = MapPreference(preference);
                IOptimizationProvider preferredProvider = Providers.FirstOrDefault(x => x.Backend == preferredBackend);

                if (preferredProvider != null && preferredProvider.IsAvailable())
                {
                    reason = $"Using explicitly requested provider {preferredProvider.DisplayName}.";
                    return preferredProvider;
                }

                IOptimizationProvider fallback = Providers.FirstOrDefault(x => x.IsAvailable());
                if (fallback != null)
                {
                    reason = $"Requested provider {preferredBackend} was unavailable; fell back to {fallback.DisplayName}.";
                    return fallback;
                }

                reason = $"Requested provider {preferredBackend} was unavailable and no fallback was detected.";
                return null;
            }

            IOptimizationProvider autoProvider = Providers.FirstOrDefault(x => x.IsAvailable());
            if (autoProvider != null)
                reason = $"Auto-detected {autoProvider.DisplayName} as the highest-priority available backend.";
            else
                reason = "No available provider was auto-detected.";

            return autoProvider;
        }

        private static OptimizationBackendPreference GetBackendPreference()
        {
            try
            {
                if (AccountManager.General != null && AccountManager.General.Exists("OptimizationBackendPreference"))
                {
                    string value = AccountManager.General.Get<string>("OptimizationBackendPreference")?.Trim();

                    if (Enum.TryParse(value, true, out OptimizationBackendPreference parsed))
                        return parsed;
                }
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Failed to parse OptimizationBackendPreference: {ex.Message}");
            }

            return OptimizationBackendPreference.Auto;
        }

        private static OptimizationBackend MapPreference(OptimizationBackendPreference preference)
        {
            switch (preference)
            {
                case OptimizationBackendPreference.NativeRoblox:
                    return OptimizationBackend.NativeRoblox;
                case OptimizationBackendPreference.Bloxstrap:
                    return OptimizationBackend.Bloxstrap;
                case OptimizationBackendPreference.Fishstrap:
                    return OptimizationBackend.Fishstrap;
                default:
                    return OptimizationBackend.Unknown;
            }
        }

        private static void UpdateDetectedSettings(OptimizationProviderInfo info)
        {
            if (AccountManager.General == null)
                return;

            AccountManager.General.Set("OptimizationDetectedBackend", info.Backend.ToString());
            AccountManager.General.Set("OptimizationDetectedProvider", info.DisplayName);
            AccountManager.General.Set("OptimizationDetectionReason", info.SelectionReason);
            string capabilityValue = string.Join(",", info.Capabilities.ToStatusList().Where(x => x.Supported).Select(x => x.Name));
            AccountManager.General.Set("OptimizationCapabilities", string.IsNullOrEmpty(capabilityValue) ? "None" : capabilityValue);
        }
    }

    internal abstract class BaseOptimizationProvider : IOptimizationProvider
    {
        public abstract OptimizationBackend Backend { get; }
        public abstract string DisplayName { get; }
        public abstract OptimizationCapabilities Capabilities { get; }

        public virtual bool IsAvailable()
        {
            return TryResolveClientSettingsPath(out _, out _);
        }

        public abstract bool TryResolveClientSettingsPath(out string settingsFilePath, out string reason);

        protected static bool IsValidVersionFolder(DirectoryInfo versionFolder)
        {
            if (versionFolder == null || !versionFolder.Exists)
                return false;

            if (!versionFolder.Name.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                return false;

            return File.Exists(Path.Combine(versionFolder.FullName, "RobloxPlayerLauncher.exe")) || File.Exists(Path.Combine(versionFolder.FullName, "RobloxPlayerBeta.exe"));
        }

        protected static string BuildClientSettingsFilePath(DirectoryInfo versionFolder)
        {
            return Path.Combine(versionFolder.FullName, "ClientSettings", "ClientAppSettings.json");
        }

        protected static string NormalizeExecutablePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return string.Empty;

            string value = rawPath.Trim();

            int commaIndex = value.IndexOf(',');
            if (commaIndex >= 0)
                value = value.Substring(0, commaIndex);

            return value.Trim().Trim('"');
        }

        protected static bool TryGetVersionFromRegistry(out DirectoryInfo versionFolder)
        {
            versionFolder = null;

            object iconValue = Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon")?.GetValue("");
            if (!(iconValue is string rawPath))
                return false;

            string executablePath = NormalizeExecutablePath(rawPath);

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return false;

            DirectoryInfo parent = Directory.GetParent(executablePath);
            if (!IsValidVersionFolder(parent))
                return false;

            versionFolder = parent;
            return true;
        }

        protected static bool TryGetLatestVersionFromRoot(string versionsRootPath, out DirectoryInfo versionFolder)
        {
            versionFolder = null;

            if (string.IsNullOrWhiteSpace(versionsRootPath) || !Directory.Exists(versionsRootPath))
                return false;

            DirectoryInfo root = new DirectoryInfo(versionsRootPath);
            DirectoryInfo latest = root.EnumerateDirectories("version-*").Where(IsValidVersionFolder).OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();

            if (latest == null)
                return false;

            versionFolder = latest;
            return true;
        }
    }

    internal sealed class NativeRobloxOptimizationProvider : BaseOptimizationProvider
    {
        public override OptimizationBackend Backend => OptimizationBackend.NativeRoblox;
        public override string DisplayName => "Native Roblox";
        public override OptimizationCapabilities Capabilities => new OptimizationCapabilities(true, true, false, false);

        public override bool TryResolveClientSettingsPath(out string settingsFilePath, out string reason)
        {
            settingsFilePath = string.Empty;

            if (TryGetVersionFromRegistry(out DirectoryInfo registryVersion))
            {
                settingsFilePath = BuildClientSettingsFilePath(registryVersion);
                reason = "Resolved from Roblox protocol registry path.";
                return true;
            }

            string localRobloxVersions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
            if (TryGetLatestVersionFromRoot(localRobloxVersions, out DirectoryInfo localVersion))
            {
                settingsFilePath = BuildClientSettingsFilePath(localVersion);
                reason = "Resolved from LocalAppData Roblox versions folder.";
                return true;
            }

            reason = "Unable to locate a valid native Roblox version folder.";
            return false;
        }
    }

    internal sealed class BloxstrapOptimizationProvider : BaseOptimizationProvider
    {
        private string RootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bloxstrap");

        public override OptimizationBackend Backend => OptimizationBackend.Bloxstrap;
        public override string DisplayName => "Bloxstrap";
        public override OptimizationCapabilities Capabilities => new OptimizationCapabilities(true, true, true, true);

        public override bool IsAvailable()
        {
            if (File.Exists(Path.Combine(RootPath, "Bloxstrap.exe")))
                return true;

            return TryGetLatestVersionFromRoot(Path.Combine(RootPath, "Versions"), out _);
        }

        public override bool TryResolveClientSettingsPath(out string settingsFilePath, out string reason)
        {
            settingsFilePath = string.Empty;

            if (TryGetVersionFromRegistry(out DirectoryInfo registryVersion) && registryVersion.FullName.IndexOf("\\Bloxstrap\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                settingsFilePath = BuildClientSettingsFilePath(registryVersion);
                reason = "Resolved from Bloxstrap-backed Roblox protocol registry path.";
                return true;
            }

            if (TryGetLatestVersionFromRoot(Path.Combine(RootPath, "Versions"), out DirectoryInfo bloxstrapVersion))
            {
                settingsFilePath = BuildClientSettingsFilePath(bloxstrapVersion);
                reason = "Resolved from LocalAppData Bloxstrap versions folder.";
                return true;
            }

            reason = "Unable to locate a valid Bloxstrap version folder.";
            return false;
        }
    }

    internal sealed class FishstrapOptimizationProvider : BaseOptimizationProvider
    {
        private string RootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fishstrap");

        public override OptimizationBackend Backend => OptimizationBackend.Fishstrap;
        public override string DisplayName => "Fishstrap";
        public override OptimizationCapabilities Capabilities => new OptimizationCapabilities(true, true, true, true);

        public override bool IsAvailable()
        {
            if (File.Exists(Path.Combine(RootPath, "Fishstrap.exe")))
                return true;

            return TryGetLatestVersionFromRoot(Path.Combine(RootPath, "Versions"), out _);
        }

        public override bool TryResolveClientSettingsPath(out string settingsFilePath, out string reason)
        {
            settingsFilePath = string.Empty;

            if (TryGetVersionFromRegistry(out DirectoryInfo registryVersion) && registryVersion.FullName.IndexOf("\\Fishstrap\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                settingsFilePath = BuildClientSettingsFilePath(registryVersion);
                reason = "Resolved from Fishstrap-backed Roblox protocol registry path.";
                return true;
            }

            if (TryGetLatestVersionFromRoot(Path.Combine(RootPath, "Versions"), out DirectoryInfo fishstrapVersion))
            {
                settingsFilePath = BuildClientSettingsFilePath(fishstrapVersion);
                reason = "Resolved from LocalAppData Fishstrap versions folder.";
                return true;
            }

            reason = "Unable to locate a valid Fishstrap version folder.";
            return false;
        }
    }
}
