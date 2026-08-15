using System;
using System.IO;
using BepInEx;
using UnityEngine;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mono.Cecil;

namespace AAAntiVmtWorm
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        private const string TargetResourceName = "Harmony.PatchInfo.bin";

        private Harmony _harmony;

        private static readonly HashSet<Assembly> _infectedAssemblies = new HashSet<Assembly>();
        private static bool _resourcePatchInstalled;

        void Awake()
        {
            try
            {
                DisinfectPluginsFolder();
                FlushPendingReplacements();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Plugin folder disinfection pass failed: {ex}");
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
                Logger.LogError($"Failed to enumerate plugins folder: {ex}");
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
                EmbeddedResource infected = moduleDef.Resources.OfType<EmbeddedResource>().FirstOrDefault(r => r.Name == TargetResourceName);

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

        private readonly List<(string original, string temp)> _pendingReplacements = new List<(string, string)>();

        private void FlushPendingReplacements()
        {
            foreach (var (original, temp) in _pendingReplacements)
            {
                try
                {
                    File.Replace(temp, original, null);
                    Logger.LogMessage($"Disinfected '{original}': removed embedded resource '{TargetResourceName}'.");
                }
                catch (IOException ex)
                {
                    Logger.LogWarning($"Could not replace '{original}': {ex.Message}.");
                    TryDeleteFile(temp);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to replace '{original}' with disinfected copy: {ex}");
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
            var findings = new List<string>();
            bool hasPatchInfoResource = false;

            try
            {
                if (asm.GetManifestResourceNames().Contains(TargetResourceName))
                {
                    hasPatchInfoResource = true;
                    findings.Add($"embedded resource '{TargetResourceName}'");
                }
            }
            catch { }

            if (findings.Count == 0)
                return;

            Logger.LogError($"Something was found in {asmName}!\nFound:");

            foreach (string finding in findings)
            {
                Logger.LogError(finding);
            }

            if (hasPatchInfoResource)
            {
                Logger.LogMessage($"Stripping embedded resource '{TargetResourceName}' from {asmName}!");
                StripEmbeddedResource(asm);
            }
        }

        
        private void StripEmbeddedResource(Assembly asm)
        {
            _infectedAssemblies.Add(asm);
            EnsureResourceHidingPatches();
        }

        private void EnsureResourceHidingPatches()
        {
            if (_resourcePatchInstalled)
                return;

            try
            {
                MethodInfo getManifestResourceStream = typeof(Assembly).GetMethod(
                    nameof(Assembly.GetManifestResourceStream),
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null
                );

                MethodInfo getManifestResourceNames = typeof(Assembly).GetMethod(
                    nameof(Assembly.GetManifestResourceNames),
                    BindingFlags.Public | BindingFlags.Instance
                );

                MethodInfo getManifestResourceInfo = typeof(Assembly).GetMethod(
                    nameof(Assembly.GetManifestResourceInfo),
                    BindingFlags.Public | BindingFlags.Instance
                );

                if (getManifestResourceStream != null)
                {
                    _harmony.Patch(
                        getManifestResourceStream,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(
                            nameof(GetManifestResourceStreamPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic))
                    );
                }

                if (getManifestResourceNames != null)
                {
                    _harmony.Patch(
                        getManifestResourceNames,
                        postfix: new HarmonyMethod(typeof(Plugin).GetMethod(
                            nameof(GetManifestResourceNamesPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic))
                    );
                }

                if (getManifestResourceInfo != null)
                {
                    _harmony.Patch(
                        getManifestResourceInfo,
                        prefix: new HarmonyMethod(typeof(Plugin).GetMethod(
                            nameof(GetManifestResourceInfoPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic))
                    );
                }

                _resourcePatchInstalled = true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to install resource-hiding patches: {ex}");
            }
        }

        private static bool GetManifestResourceStreamPrefix(Assembly __instance, string name, ref System.IO.Stream __result)
        {
            if (name == TargetResourceName && _infectedAssemblies.Contains(__instance))
            {
                __result = null;
                return false;
            }

            return true;
        }

        private static bool GetManifestResourceInfoPrefix(Assembly __instance, string resourceName, ref System.Reflection.ManifestResourceInfo __result)
        {
            if (resourceName == TargetResourceName && _infectedAssemblies.Contains(__instance))
            {
                __result = null;
                return false;
            }

            return true;
        }

        private static void GetManifestResourceNamesPostfix(Assembly __instance, ref string[] __result)
        {
            if (__result == null || !_infectedAssemblies.Contains(__instance))
                return;

            if (__result.Contains(TargetResourceName))
            {
                __result = __result.Where(n => n != TargetResourceName).ToArray();
            }
        }
    }
}