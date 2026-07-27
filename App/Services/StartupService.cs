using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace App.Services
{
    internal static class StartupService
    {
        public static async Task<bool> IsEnabledAsync()
        {
            var task = await StartupTask.GetAsync("CrsterUtilityStartup");
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        public static async Task<bool> SetEnabledAsync(bool enabled)
        {
            var task = await StartupTask.GetAsync("CrsterUtilityStartup");
            if (enabled)
            {
                if (task.State == StartupTaskState.Enabled) return true;
                return await task.RequestEnableAsync() == StartupTaskState.Enabled;
            }
            if (task.State == StartupTaskState.Enabled) task.Disable();
            return true;
        }
    }
}
