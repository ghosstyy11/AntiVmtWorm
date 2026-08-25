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
    [BepInPlugin("com.ghosty.aaantivmtworm", "AAAntiVmtWorm", "1.0.1")] // bump vers
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
            if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot))
                return;

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

            foreach (string dllPath in dlls)
            {
                try
                {
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
                    if (moduleDef.Resources[i] is EmbeddedResource er && er.Name == TargetResourceName)
                    {
                        infected = er;
                        break;
                    }
                }

                if (infected == null)
                    return;

                moduleDef.Resources.Remove(infected);

                string tempPath = dllPath + ".disinfected.tmp";
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
                    File.Replace(temp, original, null);
                    Logger.LogMessage($"Disinfected '{original}': stripped '{TargetResourceName}'.");
                }
                catch (IOException ex)
                {
                    Logger.LogWarning($"Could not replace '{original}': {ex.Message}");
                    TryDeleteFile(temp);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to replace '{original}': {ex}");
                    TryDeleteFile(temp);
                }
            }

            _pendingReplacements.Clear();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly != null)
                Check(args.LoadedAssembly);
        }

        private void Check(Assembly asm)
        {
            if (asm.IsDynamic) // dynamic assemblies cant carry the embedded resource anyway
                return;

            string asmName = asm.GetName().Name;
            bool found = false;

            try
            {
                // this checks only the end of the resource name, so it will catch any resource
                // that ends with ".bin" regardless of the namespace or prefix, thus catching
                // "Harmony.PatchInfo.bin" and others
                found = asm.GetManifestResourceNames()
                    .Any(name => name.EndsWith(TargetResourceName, StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            if (!found)
                return;

            Logger.LogError($"Found infected resource in {asmName}, stripping '{TargetResourceName}'.");

            _infectedAssemblies.Add(asm);
            EnsureResourceHidingPatches();
        }

        private void EnsureResourceHidingPatches()
        {
            if (_resourcePatchInstalled)
                return;

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
                    BindingFlags.Public | BindingFlags.Instance);

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
            if (name == TargetResourceName && _infectedAssemblies.Contains(__instance))
            {
                __result = null;
                return false;
            }
            return true;
        }

        private static bool GetManifestResourceInfoPrefix(Assembly __instance, string resourceName, ref ManifestResourceInfo __result)
        {
            if (resourceName == TargetResourceName && _infectedAssemblies.Contains(__instance))
            {
                __result = default;
                return false;
            }
            return true;
        }

        private static void GetManifestResourceNamesPostfix(Assembly __instance, ref string[] __result)
        {
            if (__result == null || !_infectedAssemblies.Contains(__instance))
                return;

            if (__result.Any(n => n == TargetResourceName))
                __result = Array.FindAll(__result, n => n != TargetResourceName);
        }
    }
}
