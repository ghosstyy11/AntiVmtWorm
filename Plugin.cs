using BepInEx;
using HarmonyLib;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AAAntiVmtWorm
{
    [BepInPlugin("com.ghosty.aaantivmtworm", "AAAntiVmtWorm", "1.0.2")] // bump vers
    public class Plugin : BaseUnityPlugin
    {
        // i changed "Harmony.PatchInfo.bin" to .bin, because REALISTICALLY
        // no mod should EVER have a .bin file in their assembly. if you
        // want to change it back, you can, but I recommend leaving it as
        // .bin though.
        private const string TargetResourceName = ".bin";

        private Harmony _harmony;
        private static readonly HashSet<Assembly> _infectedAssemblies = new HashSet<Assembly>();
        private static bool _resourcePatchInstalled;
        private readonly List<(string original, string temp)> _pendingReplacements = new List<(string, string)>();

        void Awake()
        {
            // init the harmony so it works
            _harmony = new Harmony("com.ghosty.aaantivmtworm");

            try
            {
                DisinfectPluginsFolder();
                FlushPendingReplacements();
                ScanLoadedPluginAssemblies();
                EnsureResourceHidingPatches();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Disinfection pass failed: {ex}");
            }

            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private void DisinfectPluginsFolder()
        {
            string pluginsRoot = Paths.PluginPath;
            if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot)) return;

            string[] dlls;
            try
            {
                dlls = Directory.GetFiles(pluginsRoot, "*.dll", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to enumerate plugins: {ex}");
                return;
            }

            string ownAssemblyPath = null;
            try { ownAssemblyPath = typeof(Plugin).Assembly.Location; } catch { }

            foreach (string dllPath in dlls)
            {
                try
                {
                    if (!string.IsNullOrEmpty(ownAssemblyPath) && PathsEqual(dllPath, ownAssemblyPath)) continue;
                    TryDisinfectFile(dllPath);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to inspect '{dllPath}': {ex}");
                }
            }
        }

        private void TryDisinfectFile(string dllPath)
        {
            using (var moduleDef = ModuleDefinition.ReadModule(dllPath))
            {
                EmbeddedResource infected = null;

                for (int i = 0; i < moduleDef.Resources.Count; i++)
                {
                    if (moduleDef.Resources[i] is EmbeddedResource er && er.Name.EndsWith(TargetResourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        infected = er;
                        break;
                    }
                }

                if (infected == null) return;

                moduleDef.Resources.Remove(infected);

                string tempPath = dllPath + ".disinfected.tmp";
                TryDeleteFile(tempPath);

                try
                {
                    moduleDef.Write(tempPath);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to write disinfected copy of '{dllPath}': {ex}");
                    TryDeleteFile(tempPath);
                    return;
                }

                _pendingReplacements.Add((dllPath, tempPath));
            }
        }

        private void FlushPendingReplacements()
        {
            foreach (var (original, temp) in _pendingReplacements)
            {
                try
                {
                    ReplaceFile(temp, original);
                    Logger.LogMessage($"Disinfected '{original}': stripped resources ending with '{TargetResourceName}'.");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to replace '{original}': {ex}");
                    TryDeleteFile(temp);
                }
            }

            _pendingReplacements.Clear();
        }

        private static void ReplaceFile(string temp, string original)
        {
            try
            {
                File.Replace(temp, original, null);
                return;
            }
            catch { }

            if (File.Exists(original)) File.Delete(original);
            File.Move(temp, original);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private void ScanLoadedPluginAssemblies()
        {
            Assembly[] assemblies;

            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to enumerate loaded assemblies: {ex}");
                return;
            }

            foreach (Assembly assembly in assemblies)
            {
                try { Check(assembly); }
                catch (Exception ex) { Logger.LogError($"Failed to inspect loaded assembly: {ex}"); }
            }
        }

        void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly != null)
            {
                try { Check(args.LoadedAssembly); }
                catch (Exception ex) { Logger.LogError($"Failed to inspect loaded assembly: {ex}"); }
            }
        }

        private void Check(Assembly asm)
        {
            if (asm == null || asm.IsDynamic) return;
            if (!IsPluginAssembly(asm)) return;

            string asmName;

            try { asmName = asm.GetName().Name; }
            catch { asmName = "<unknown>"; }

            bool found = false;

            try
            {
                // this checks only the end of the resource name, so it will catch any resource
                // that ends with ".bin" regardless of the namespace or prefix, thus catching
                // "Harmony.PatchInfo.bin" and others
                found = asm.GetManifestResourceNames().Any(name => name.EndsWith(TargetResourceName, StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            if (!found) return;

            Logger.LogError($"Found infected resource in {asmName}, stripping '{TargetResourceName}'.");

            _infectedAssemblies.Add(asm);
            EnsureResourceHidingPatches();
        }

        private bool IsPluginAssembly(Assembly asm)
        {
            string location;

            try { location = asm.Location; }
            catch { return false; }

            if (string.IsNullOrEmpty(location)) return false;

            string pluginRoot;

            try
            {
                pluginRoot = Path.GetFullPath(Paths.PluginPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                location = Path.GetFullPath(location);
            }
            catch { return false; }

            if (!location.StartsWith(pluginRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !location.StartsWith(pluginRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            return location.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                first = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                second = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void EnsureResourceHidingPatches()
        {
            if (_resourcePatchInstalled) return;

            try
            {
                var pluginType = typeof(Plugin);
                var flags = BindingFlags.Static | BindingFlags.NonPublic;

                var streamMethod = typeof(Assembly).GetMethod(
                    nameof(Assembly.GetManifestResourceStream),
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);

                var namesMethod = typeof(Assembly).GetMethod(
                    nameof(Assembly.GetManifestResourceNames),
                    BindingFlags.Public | BindingFlags.Instance);

                var infoMethod = typeof(Assembly).GetMethod(
                    nameof(Assembly.GetManifestResourceInfo),
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);

                if (streamMethod != null)
                    _harmony.Patch(streamMethod, prefix: new HarmonyMethod(pluginType.GetMethod(nameof(GetManifestResourceStreamPrefix), flags)));

                if (namesMethod != null)
                    _harmony.Patch(namesMethod, postfix: new HarmonyMethod(pluginType.GetMethod(nameof(GetManifestResourceNamesPostfix), flags)));

                if (infoMethod != null)
                    _harmony.Patch(infoMethod, prefix: new HarmonyMethod(pluginType.GetMethod(nameof(GetManifestResourceInfoPrefix), flags)));

                _resourcePatchInstalled = true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to install resource-hiding patches: {ex}");
            }
        }

        private static bool GetManifestResourceStreamPrefix(Assembly __instance, string name, ref Stream __result)
        {
            if (_infectedAssemblies.Contains(__instance) && !string.IsNullOrEmpty(name) && name.EndsWith(TargetResourceName, StringComparison.OrdinalIgnoreCase))
            {
                __result = null;
                return false;
            }

            return true;
        }

        private static bool GetManifestResourceInfoPrefix(Assembly __instance, string resourceName, ref ManifestResourceInfo __result)
        {
            if (_infectedAssemblies.Contains(__instance) && !string.IsNullOrEmpty(resourceName) && resourceName.EndsWith(TargetResourceName, StringComparison.OrdinalIgnoreCase))
            {
                __result = null;
                return false;
            }

            return true;
        }

        private static void GetManifestResourceNamesPostfix(Assembly __instance, ref string[] __result)
        {
            if (__result == null || !_infectedAssemblies.Contains(__instance)) return;

            __result = __result.Where(n => !n.EndsWith(TargetResourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }
}
