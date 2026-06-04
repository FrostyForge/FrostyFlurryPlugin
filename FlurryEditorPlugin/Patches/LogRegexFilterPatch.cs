using Frosty.Core;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Flurry.Editor.Patches
{
    [HarmonyPatch(typeof(FrostyCore.FrostyLogger))]
    [HarmonyPatchCategory("flurry.editor")]
    public static class LogRegexFilterPatch
    {
        private static readonly object cacheLock = new object();
        private static string cachedPatternSource = null;
        private static List<Regex> cachedRegexes = new List<Regex>();

        [HarmonyPatch("Log")]
        [HarmonyPrefix]
        public static bool Log_Prefix(string text, object[] vars) => ShouldAllow(text, vars);

        [HarmonyPatch("LogWarning")]
        [HarmonyPrefix]
        public static bool LogWarning_Prefix(string text, object[] vars) => ShouldAllow(text, vars);

        [HarmonyPatch("LogError")]
        [HarmonyPrefix]
        public static bool LogError_Prefix(string text, object[] vars) => ShouldAllow(text, vars);

        private static bool ShouldAllow(string text, object[] vars)
        {
            string resolvedMessage = ResolveMessage(text, vars);
            if (string.IsNullOrWhiteSpace(resolvedMessage))
            {
                return true;
            }

            foreach (Regex pattern in GetPatterns())
            {
                try
                {
                    if (pattern.IsMatch(resolvedMessage))
                    {
                        return false;
                    }
                }
                catch
                {
                    // Ignore malformed runtime states for individual matches.
                }
            }

            return true;
        }

        private static string ResolveMessage(string text, object[] vars)
        {
            string template = text ?? string.Empty;
            if (vars == null || vars.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, vars);
            }
            catch
            {
                return template;
            }
        }

        private static IReadOnlyList<Regex> GetPatterns()
        {
            string source = string.Empty;
            try
            {
                source = Config.Get<string>("Flurry.BlockedLogRegexPatterns", string.Empty) ?? string.Empty;
            }
            catch
            {
                source = string.Empty;
            }

            lock (cacheLock)
            {
                if (string.Equals(source, cachedPatternSource, StringComparison.Ordinal))
                {
                    return cachedRegexes;
                }

                List<Regex> compiled = new List<Regex>();
                string[] rawParts = source.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in rawParts)
                {
                    string pattern = raw.Trim();
                    if (pattern.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        compiled.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                    }
                    catch
                    {
                        // Ignore invalid expressions and continue.
                    }
                }

                cachedPatternSource = source;
                cachedRegexes = compiled;
                return cachedRegexes;
            }
        }
    }
}
