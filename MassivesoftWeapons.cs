using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using MassivesoftCore;
using System.Reflection;
using SPTarkov.Server.Core.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Loaders;

namespace MassivesoftWeapons
{
    public record ModMetadata : AbstractModMetadata
    {
        public override string Name { get; init; } = "MassivesoftWeapons";
        public override string Author { get; init; } = "Massivesoft";
        public override List<string>? Contributors { get; init; }
        public override SemanticVersioning.Version Version { get; init; } = new("1.1.2");
        public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");


        public override List<string>? Incompatibilities { get; init; }
        public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new Dictionary<string, SemanticVersioning.Range>{
            { "com.massivesoft.massivesoftcore", new SemanticVersioning.Range(">=1.0.0") }
        };
        public override string? Url { get; init; }
        public override bool? IsBundleMod { get; init; } = false;
        public override string? License { get; init; } = "MIT";
        public override string ModGuid { get; init; } = "com.massivesoft.massivesoftweapons";
    }

    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
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
        public Task OnLoad()
        {
            string modName = new ModMetadata().Name;
            var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

            //Load Item Jsons
            DirectoryInfo directoryInfoSubs = new DirectoryInfo(Path.Combine(pathToMod, "subs"));
            if (!directoryInfoSubs.Exists)
            {
                return Task.CompletedTask;
            }
            foreach (DirectoryInfo directoryInfoSubFolders in directoryInfoSubs.GetDirectories())
            {

                _ = LoadBundlesAsync(directoryInfoSubFolders.FullName);
                DirectoryInfo directoryInfoDB = new DirectoryInfo(Path.Combine(directoryInfoSubFolders.FullName, "db"));
                if (!directoryInfoDB.Exists)
                {
                    continue;
                }
                foreach (FileInfo file in directoryInfoDB.GetFiles())
                {
                    if (file.Extension == ".json")
                    {
                        string json = fileUtil.ReadFile(file.FullName);

                        try
                        {
                            JsonNode? node = JsonNode.Parse(json);
                            if (node is JsonObject rootObj)
                            {
                                rootObj.Remove("$schema");
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
            return Task.CompletedTask;
        }
        public async Task LoadBundlesAsync(string pathBundle)
        {
            await bundleHashCacheService.HydrateCache();

            var modPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), pathBundle);

            var modBundles = await jsonUtil.DeserializeFromFileAsync<BundleManifest>(
                Path.Join(pathBundle, "bundles.json")
            );

            var bundleManifests = modBundles?.Manifest ?? [];

            foreach (var bundleManifest in bundleManifests)
            {
                var relativeModPath = modPath.Replace('\\', '/');

                var bundleLocalPath = Path.Join(relativeModPath, "bundles", bundleManifest.Key).Replace('\\', '/');

                if (!File.Exists(bundleLocalPath))
                {
                    logger.Warning($"Could not find bundle {bundleManifest.Key}");
                    continue;
                }

                var bundleHash = await bundleHashCacheService.CalculateMatchAndStoreHash(bundleLocalPath);

                bundleLoader.AddBundle(bundleManifest.Key, new BundleInfo(relativeModPath, bundleManifest, bundleHash));
            }
        }
    }
}

