namespace RBX_Alt_Manager.Classes
{
    public static class ClientSettingsPatcher
    {
        public static void PatchSettings()
        {
            var report = OptimizationService.ApplyLaunchSettings();

            if (!report.Success)
                Program.Logger.Error($"Client settings patch failed: {report.Reason}");
        }
    }
}
