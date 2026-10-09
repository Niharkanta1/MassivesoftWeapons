using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Bundles;
using MassivesoftCore;
using System.Reflection;
using SPTarkov.Server.Core.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Loaders;

namespace MassivesoftWeapons
{
    public record ModMetadata : IModMetadata
    {
        public string Name { get; init; } = "MassivesoftWeapons";
        public string Author { get; init; } = "Massivesoft";
        public List<string>? Contributors { get; init; }
        public SemanticVersioning.Version Version { get; init; } = new("1.1.2");
        public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");


        public List<string>? Incompatibilities { get; init; }
        public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new Dictionary<string, SemanticVersioning.Range>{
            { "com.massivesoft.massivesoftcore", new SemanticVersioning.Range(">=1.0.0") }
        };
        public string? Url { get; init; }
        public string License { get; init; } = "MIT";
        public string ModGuid { get; init; } = "com.massivesoft.massivesoftweapons";
        public bool HasPrepatcher { get; init; }
    }

    [Injectable(TypePriority = OnLoadOrder.Preload + 2)]
    public class MassivesoftBaseItemsClass(
        ISptLogger<MassivesoftBaseItemsClass> logger,
        MassivesoftCoreClass massivesoftCoreClass,
        ModHelper modHelper,
        FileUtil fileUtil,
        BundleHashCacheService bundleHashCacheService,
        JsonUtil jsonUtil,
        BundleLoader bundleLoader
        ) : IOnLoad
    {
        public async Task OnLoadAsync(CancellationToken cancellationToken)
        {
            string modName = new ModMetadata().Name;
            var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

            //Load Item Jsons
            DirectoryInfo directoryInfoSubs = new DirectoryInfo(Path.Combine(pathToMod, "subs"));
            if (!directoryInfoSubs.Exists)
            {
                return;
            }
            await bundleHashCacheService.HydrateCacheAsync(cancellationToken);

            foreach (DirectoryInfo directoryInfoSubFolders in directoryInfoSubs.GetDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(Path.Combine(directoryInfoSubFolders.FullName, "bundles.json")))
                    {
                        await LoadBundlesAsync(directoryInfoSubFolders.FullName, cancellationToken);
                    }
                    else
                    {
                        logger.Debug($"[{modName}] No bundles.json in sub folder {directoryInfoSubFolders.Name}");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.Error($"[{modName}] Failed to load bundles of sub folder {directoryInfoSubFolders.Name}: {exception}");
                }
                DirectoryInfo directoryInfoDB = new DirectoryInfo(Path.Combine(directoryInfoSubFolders.FullName, "db"));
                if (!directoryInfoDB.Exists)
                {
                    continue;
                }
                foreach (FileInfo file in directoryInfoDB.GetFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.Extension == ".json")
                    {
                        string json = fileUtil.ReadFile(file.FullName);

                        try
                        {
                            JsonNode? node = JsonNode.Parse(json);
                            if (node is JsonObject rootObj)
                            {
                                rootObj.Remove("$schema");
                                SanitizeIds(rootObj, file.FullName, modName);
                                json = rootObj.ToJsonString();
                            }
                            Dictionary<string, AdvancedNewItemFromCloneDetails>? details = JsonSerializer.Deserialize<Dictionary<string, AdvancedNewItemFromCloneDetails>>(json, JsonUtil.JsonSerializerOptionsNoIndent);
                            if (details == null || details.Count == 0)
                            {
                                logger.Error($"Json file {file.Name} of {modName} is invalid (null)!");
                                continue;
                            }
                            foreach (KeyValuePair<string, AdvancedNewItemFromCloneDetails> pair in details)
                            {
                                massivesoftCoreClass.AdvancedCreateItemFromClone(pair.Value);
                            }
                        }
                        catch (JsonException exception)
                        {
                            logger.Error($"Json file {file.Name} of {modName} is invalid! {exception.Message}");
                        }
                    }
                }
            }
            logger.Info("Finished loading all Massivesoft Weapons.");
        }

        private static readonly HashSet<string> IdProperties = ["_id", "_parent", "_proto", "_tpl"];

        // SPT 4.1 requires 24-char hex ids; legacy JSONs use readable ids, so map them deterministically.
        private void SanitizeIds(JsonObject root, string fileName, string modName)
        {
            foreach (var item in root)
            {
                if (item.Value is not JsonObject itemObj)
                {
                    continue;
                }
                string? ownId = itemObj["newId"]?.GetValue<string>();
                Walk(itemObj, item.Key, ownId, fileName, modName);
            }
        }

        private void Walk(JsonNode? node, string itemKey, string? ownId, string fileName, string modName)
        {
            if (node is JsonObject obj)
            {
                foreach (var prop in obj.ToList())
                {
                    if (IdProperties.Contains(prop.Key) && prop.Value is JsonValue value && value.TryGetValue<string>(out var text) && !IsHexId(text))
                    {
                        string replacement = prop.Key == "_parent" && text == itemKey && ownId != null ? ownId : HashToId(text);
                        logger.Debug($"[{modName}] {Path.GetFileName(fileName)}: '{prop.Key}' value '{text}' is not a valid id, using {replacement}");
                        obj[prop.Key] = replacement;
                    }
                    else
                    {
                        Walk(prop.Value, itemKey, ownId, fileName, modName);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var element in array)
                {
                    Walk(element, itemKey, ownId, fileName, modName);
                }
            }
        }

        private static bool IsHexId(string s) => s.Length == 24 && s.All(Uri.IsHexDigit);

        private static string HashToId(string s) =>
            Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s)), 0, 12).ToLowerInvariant();

        private async Task LoadBundlesAsync(string pathBundle, CancellationToken cancellationToken)
        {
            var modPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), pathBundle);

            var modBundles = await jsonUtil.DeserializeFromFileAsync<BundleManifest>(
                Path.Join(pathBundle, "bundles.json"),
                cancellationToken
            );

            var bundleManifests = modBundles?.Manifest ?? [];

            foreach (var bundleManifest in bundleManifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeModPath = modPath.Replace('\\', '/');

                var bundleLocalPath = Path.Join(relativeModPath, "bundles", bundleManifest.Key).Replace('\\', '/');

                if (!File.Exists(bundleLocalPath))
                {
                    logger.Warning($"Could not find bundle {bundleManifest.Key}");
                    continue;
                }

                var bundleHash = await bundleHashCacheService.GetOrCalculateHashAsync(bundleLocalPath, cancellationToken);
                if (bundleHash == null)
                {
                    logger.Warning($"Could not calculate a valid bundle hash for {bundleManifest.Key}");
                    continue;
                }

                bundleLoader.AddBundle(bundleManifest.Key, new BundleInfo
                {
                    ModPath = relativeModPath,
                    Bundle = bundleManifest,
                    Crc = bundleHash.Crc,
                    Size = bundleHash.Size,
                    ModifiedUtcTicks = bundleHash.ModifiedUtcTicks
                });
            }
        }
    }
}
