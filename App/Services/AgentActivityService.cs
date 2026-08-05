using System;
using System.Collections.Generic;

namespace App.Services
{
    internal static class AgentActivityService
    {
        private static readonly HashSet<string> ActiveKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Lock = new();

        public static event EventHandler? ActivityChanged;

        public static void SetActive(string key, bool active)
        {
            bool changed;
            lock (Lock)
            {
                changed = active ? ActiveKeys.Add(key) : ActiveKeys.Remove(key);
            }
            if (changed)
                ActivityChanged?.Invoke(null, EventArgs.Empty);
        }

        public static bool IsActive(string key)
        {
            lock (Lock)
            {
                return ActiveKeys.Contains(key);
            }
        }
    }
}
