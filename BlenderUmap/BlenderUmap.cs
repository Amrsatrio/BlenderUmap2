﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using BlenderUmap.Extensions;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SkiaSharp;
using static CUE4Parse.UE4.Assets.Exports.Texture.EPixelFormat;

namespace BlenderUmap {
    public static class Program {
        public static Config config;
        public static MyFileProvider provider;
        private static readonly long start = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        public static void Main(string[] args) {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .CreateLogger();

            try {
                var configFile = new FileInfo("config.json");
                if (!configFile.Exists) {
                    Log.Error("config.json not found");
                    return;
                }

                Log.Information("Reading config file {0}", configFile.FullName);

                using (var reader = configFile.OpenText()) {
                    config = new JsonSerializer().Deserialize<Config>(new JsonTextReader(reader));
                }

                var paksDir = config.PaksDirectory;
                if (!Directory.Exists(paksDir)) {
                    throw new MainException("Directory " + Path.GetFullPath(paksDir) + " not found.");
                }

                if (string.IsNullOrEmpty(config.ExportPackage)) {
                    throw new MainException("Please specify ExportPackage.");
                }

                provider = new MyFileProvider(paksDir, config.Game, config.EncryptionKeys, config.bDumpAssets, config.ObjectCacheSize);
                provider.LoadVirtualPaths();
                var newestUsmap = GetNewestUsmap(new DirectoryInfo("mappings"));
                if (newestUsmap != null) {
                    var usmap = new FileUsmapTypeMappingsProvider(newestUsmap.FullName);
                    usmap.Reload();
                    provider.MappingsContainer = usmap;
                    Log.Information("Loaded mappings from {0}", newestUsmap.FullName);
                }
                else {
                    provider.LoadMappings();
                }

                var pkg = ExportAndProduceProcessed(config.ExportPackage);
                if (pkg == null) return;

                while (ThreadPool.PendingWorkItemCount == 0) { }
                var file = new FileInfo("processed.json");
                Log.Information("Writing to {0}", file.FullName);
                using (var writer = file.CreateText()) {
                    var pkgName = provider.CompactFilePath(pkg.Name);
                    new JsonSerializer().Serialize(writer, pkgName);
                }

                Log.Information("All done in {0:F1} sec. In the Python script, replace the line with data_dir with this line below:\n\ndata_dir = r\"{1}\"", (DateTimeOffset.Now.ToUnixTimeMilliseconds() - start) / 1000.0F, Directory.GetCurrentDirectory());
            } catch (Exception e) {
                if (e is MainException) {
                    Log.Information(e.Message);
                } else {
                    Log.Error(e, "An unexpected error has occurred, please report");
                }
                Environment.Exit(1);
            }
        }

        public static FileInfo GetNewestUsmap(DirectoryInfo directory) {
            FileInfo chosenFile = null;
            if (!directory.Exists) return null;
            var files = directory.GetFiles().OrderByDescending(f => f.LastWriteTime);
            foreach (var f in files) {
                if (f.Extension == ".usmap") {
                    chosenFile = f;
                    break;
                }
            }
            return chosenFile;
        }

        private static IPackage ExportAndProduceProcessed(string path) {
            if (path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                path = $"{path.SubstringBeforeLast(".")}.{path.SubstringAfterLast("/").SubstringBeforeLast(".")}";

            if (!provider.TryLoadObject(path, out var obj)) {
                Log.Warning("Object {0} not found", path);
                return null;
            }

            if (obj.ExportType == "FortPlaysetItemDefinition") {
                FortPlaysetItemDefinition.ExportAndProduceProcessed(obj);
                return obj.Owner;
            }

            if (obj is not UWorld world) {
                Log.Information("{0} is not a World, won't try to export", obj.GetPathName());
                return null;
            }

            var persistentLevel = world.PersistentLevel.Load<ULevel>();
            var comps = new JArray();

            for (var index = 0; index < persistentLevel.Actors.Length; index++) {
                var actorLazy = persistentLevel.Actors[index];
                if (actorLazy == null || actorLazy.IsNull) continue;
                var actor = actorLazy.Load();
                if (actor.ExportType == "LODActor") continue;
                Log.Information("Loading {0}: {1}/{2} {3}",world.Name, index,persistentLevel.Actors.Length, actorLazy);

                var staticMeshCompLazy = actor.GetOrDefault<FPackageIndex>("StaticMeshComponent", new FPackageIndex()); // /Script/Engine.StaticMeshActor:StaticMeshComponent or /Script/FortniteGame.BuildingSMActor:StaticMeshComponent
                if (staticMeshCompLazy.IsNull) continue;
                var staticMeshComp = staticMeshCompLazy?.Load();

                // identifiers
                var comp = new JArray();
                comps.Add(comp);
                comp.Add(actor.TryGetValue<FGuid>(out var guid, "MyGuid") // /Script/FortniteGame.BuildingActor:MyGuid
                    ? guid.ToString(EGuidFormats.Digits).ToLowerInvariant()
                    : Guid.NewGuid().ToString().Replace("-", ""));
                comp.Add(actor.Name);

                // region mesh
                var mesh = staticMeshComp.GetOrDefault<FPackageIndex>("StaticMesh"); // /Script/Engine.StaticMeshComponent:StaticMesh

                if (mesh.IsNull) { // read the actor class to find the mesh
                    var actorBlueprint = actor.Class;

                    if (actorBlueprint is UBlueprintGeneratedClass) {
                        foreach (var actorExp in actorBlueprint.Owner.GetExports()) {
                            if ((mesh = actorExp.GetOrDefault<FPackageIndex>("StaticMesh")) != null) {
                                break;
                            }
                        }
                    }
                }
                // endregion

                var matsObj = new JObject(); // matpath: [4x[str]]
                var textureDataArr = new JArray();
                var materials = new List<Mat>();
                ExportMesh(mesh, materials);

                if (config.bReadMaterials /*&& actor is BuildingSMActor*/) {
                    var material = actor.GetOrDefault<FPackageIndex>("BaseMaterial"); // /Script/FortniteGame.BuildingSMActor:BaseMaterial
                    var overrideMaterials = staticMeshComp.GetOrDefault<List<FPackageIndex>>("OverrideMaterials"); // /Script/Engine.MeshComponent:OverrideMaterials

                    foreach (var textureDataIdx in actor.GetProps<FPackageIndex>("TextureData")) { // /Script/FortniteGame.BuildingSMActor:TextureData
                        var td = textureDataIdx?.Load();

                        if (td != null) {
                            var textures = new JArray();
                            AddToArray(textures, td.GetOrDefault<FPackageIndex>("Diffuse"));
                            AddToArray(textures, td.GetOrDefault<FPackageIndex>("Normal"));
                            AddToArray(textures, td.GetOrDefault<FPackageIndex>("Specular"));
                            textureDataArr.Add(new JArray { PackageIndexToDirPath(textureDataIdx), textures });
                            var overrideMaterial = td.GetOrDefault<FPackageIndex>("OverrideMaterial");
                            if (overrideMaterial != null) {
                                material = overrideMaterial;
                            }
                        } else {
                            textureDataArr.Add(JValue.CreateNull());
                        }
                    }

                    for (int i = 0; i < materials.Count; i++) {
                        var mat = materials[i];
                        if (material != null) {
                            mat.Material = overrideMaterials != null && i < overrideMaterials.Count && overrideMaterials[i] != null ? overrideMaterials[i] : material;
                        }

                        mat.PopulateTextures();
                        mat.AddToObj(matsObj);
                    }
                }

                // region additional worlds
                var children = new JArray();
                var additionalWorlds = actor.GetOrDefault<List<FSoftObjectPath>>("AdditionalWorlds"); // /Script/FortniteGame.BuildingFoundation:AdditionalWorlds

                if (config.bExportBuildingFoundations && additionalWorlds != null) {
                    foreach (var additionalWorld in additionalWorlds) {
                        var text = additionalWorld.AssetPathName.Text;
                        var childPackage = ExportAndProduceProcessed(text);
                        children.Add(childPackage != null ? provider.CompactFilePath(childPackage.Name) : null);
                    }
                }
                // endregion

                comp.Add(PackageIndexToDirPath(mesh));
                comp.Add(matsObj);
                comp.Add(textureDataArr);
                comp.Add(Vector(staticMeshComp.GetOrDefault<FVector>("RelativeLocation"))); // /Script/Engine.SceneComponent:RelativeLocation
                comp.Add(Rotator(staticMeshComp.GetOrDefault<FRotator>("RelativeRotation"))); // /Script/Engine.SceneComponent:RelativeRotation
                comp.Add(Vector(staticMeshComp.GetOrDefault<FVector>("RelativeScale3D", FVector.OneVector))); // /Script/Engine.SceneComponent:RelativeScale3D
                comp.Add(children);
            }

            /*if (config.bExportBuildingFoundations) {
                foreach (var streamingLevelLazy in world.StreamingLevels) {
                    UObject streamingLevel = streamingLevelLazy.Load();
                    if (streamingLevel == null) continue;

                    var children = new JArray();
                    string text = streamingLevel.GetOrDefault<FSoftObjectPath>("WorldAsset").AssetPathName.Text;
                    var cpkg = ExportAndProduceProcessed(text.SubstringBeforeLast('.'));
                    children.Add(cpkg != null ? provider.CompactFilePath(cpkg.Name) : null);

                    var transform = streamingLevel.GetOrDefault<FTransform>("LevelTransform");

                    var comp = new JArray {
                        JValue.CreateNull(), // GUID
                        streamingLevel.Name,
                        JValue.CreateNull(), // mesh path
                        JValue.CreateNull(), // materials
                        JValue.CreateNull(), // texture data
                        Vector(transform.Translation), // location
                        Quat(transform.Rotation), // rotation
                        Vector(transform.Scale3D), // scale
                        children
                    };
                    comps.Add(comp);
                }
            }*/

            var pkg = world.Owner;
            string pkgName = provider.CompactFilePath(pkg.Name).SubstringAfter("/");
            var file = new FileInfo(Path.Combine(MyFileProvider.JSONS_FOLDER.ToString(), pkgName + ".processed.json"));
            file.Directory.Create();
            Log.Information("Writing to {0}", file.FullName);

            using var writer = file.CreateText();
            new JsonSerializer().Serialize(writer, comps);

            return pkg;
        }

        public static void AddToArray(JArray array, FPackageIndex index) {
            if (index != null) {
                ExportTexture(index);
                array.Add(PackageIndexToDirPath(index));
            } else {
                array.Add(JValue.CreateNull());
            }
        }

        private static void ExportTexture(FPackageIndex index) {
            try {
                var obj = index.Load();
                if (obj is not UTexture2D texture) {
                    return;
                }

                // CUE4Parse only reads the first FTexturePlatformData and drops the rest
                var firstMip = texture.GetFirstMip(); // Modify this if you want lower res textures
                char[] fourCC = config.bExportToDDSWhenPossible ? GetDDSFourCC(texture) : null;
                var output = new FileInfo(Path.Combine(GetExportDir(texture).ToString(), texture.Name + (fourCC != null ? ".dds" : ".png")));

                if (output.Exists) {
                    Log.Debug("Texture already exists, skipping: {0}", output.FullName);
                } else {
                    if (fourCC != null) {
                        throw new NotImplementedException("DDS export is not implemented");
                    }

                    ThreadPool.QueueUserWorkItem((x) => {
                        Log.Information("Saving texture to {0}", output.FullName);
                        using var image = texture.Decode(firstMip);
                        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                        try {
                            using var stream = output.OpenWrite();
                            data.SaveTo(stream);
                        }
                        catch (IOException) { } // two threads trying to write same texture
                    });
                }
            } catch (Exception e) {
                Log.Warning(e, "Failed to save texture");
            }
        }

        public static void ExportMesh(FPackageIndex mesh, List<Mat> materials) {
            var meshExport = mesh?.Load<UStaticMesh>();
            if (meshExport == null) return;
            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    var exporter = new MeshExporter(meshExport, exportMaterials: false);
                    if (exporter.MeshLods.Count == 0) {
                        Log.Warning("Mesh '{0}' has no LODs", meshExport.Name);
                        return;
                    }
                    File.WriteAllBytes(Path.Combine(GetExportDir(meshExport).ToString(), meshExport.Name + ".pskx"), exporter.MeshLods.First().FileData); 
                }
                catch (IOException) { } // two threads trying to write same mesh
            });

            if (config.bReadMaterials) {
                var staticMaterials = meshExport.StaticMaterials;
                if (staticMaterials != null) {
                    foreach (var staticMaterial in staticMaterials) {
                        materials.Add(new Mat(staticMaterial.MaterialInterface));
                    }
                }
            }
        }

        public static DirectoryInfo GetExportDir(UObject exportObj) => GetExportDir(exportObj.Owner);

        public static DirectoryInfo GetExportDir(IPackage package) {
            string pkgPath = provider.CompactFilePath(package.Name);
            pkgPath = pkgPath.SubstringBeforeLast('.');

            if (pkgPath.StartsWith("/")) {
                pkgPath = pkgPath[1..];
            }

            var outputDir = new FileInfo(pkgPath).Directory;
            string pkgName = pkgPath.SubstringAfterLast('/');

            // what's this for?
            // if (exportObj.Name != pkgName) {
            //     outputDir = new DirectoryInfo(Path.Combine(outputDir.ToString(), pkgName));
            // }

            outputDir.Create();
            return outputDir;
        }

        public static string PackageIndexToDirPath(FPackageIndex index) {
            var obj = index?.ResolvedObject;
            if (obj == null) return null;

            string pkgPath = provider.CompactFilePath(obj.Package.Name);
            pkgPath = pkgPath.SubstringBeforeLast('.');
            var objectName = obj.Name.Text;
            return pkgPath.SubstringAfterLast('/') == objectName ? pkgPath : pkgPath + '/' + objectName;
        }

        public static JArray Vector(FVector vector) => new() {vector.X, vector.Y, vector.Z};
        public static JArray Rotator(FRotator rotator) => new() {rotator.Pitch, rotator.Yaw, rotator.Roll};
        public static JArray Quat(FQuat quat) => new() {quat.X, quat.Y, quat.Z, quat.W};

        private static char[] GetDDSFourCC(UTexture2D texture) => (texture.Format switch {
            PF_DXT1 => "DXT1",
            PF_DXT3 => "DXT3",
            PF_DXT5 => "DXT5",
            PF_BC4 => "ATI1",
            PF_BC5 => "ATI2",
            _ => null
        })?.ToCharArray();

        public static T[] GetProps<T>(this IPropertyHolder obj, string name) {
            var collected = new List<FPropertyTag>();
            var maxIndex = -1;
            foreach (var prop in obj.Properties) {
                if (prop.Name.Text == name) {
                    collected.Add(prop);
                    maxIndex = Math.Max(maxIndex, prop.ArrayIndex);
                }
            }

            var array = new T[maxIndex + 1];
            foreach (var prop in collected) {
                array[prop.ArrayIndex] = (T) prop.Tag.GetValue(typeof(T));
            }

            return array;
        }
        
        private static T GetAnyValueOrDefault<T>(this Dictionary<string, T> dict, string[] keys) {
            foreach (var key in keys) {
                if (dict.ContainsKey(key))
                    return dict.GetValueOrDefault(key);
            }
            return default;
        }

        public class Mat {
            public FPackageIndex Material;
            private readonly Dictionary<string, FPackageIndex> _textureMap = new();

            public Mat(FPackageIndex material) {
                Material = material;
            }

            public void PopulateTextures() {
                PopulateTextures(Material?.Load());
            }

            private void PopulateTextures(UObject obj) {
                if (obj is not UMaterialInstance material) {
                    return;
                }

                var textureParameterValues =
                    material.GetOrDefault<List<FTextureParameterValue>>("TextureParameterValues");
                if (textureParameterValues != null) {
                    foreach (var textureParameterValue in textureParameterValues) {
                        var name = textureParameterValue.ParameterInfo.Name;
                        if (!name.IsNone) {
                            var parameterValue = textureParameterValue.ParameterValue;
                            if (!_textureMap.ContainsKey(name.Text)) {
                                _textureMap[name.Text] = parameterValue;
                            }
                        }
                    }
                }

                if (material.Parent != null) {
                    PopulateTextures(material.Parent);
                }
            }

            public void AddToObj(JObject obj) {
                if (Material == null || Material.IsNull) {
                    obj.Add(GetHashCode().ToString("x"), null);
                    return;
                }

                FPackageIndex[][] textures = { // d n s e a
                    new[] {
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV1.Diffuse),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV1.Normal),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV1.Specular),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV1.Emission),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV1.MaskTexture)
                    },
                    new[] {
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV2.Diffuse),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV2.Normal),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV2.Specular),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV2.Emission),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV2.MaskTexture)
                    },
                    new[] {
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV3.Diffuse),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV3.Normal),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV3.Specular),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV3.Emission),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV3.MaskTexture)
                    },
                    new[] {
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV4.Diffuse),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV4.Normal),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV4.Specular),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV4.Emission),
                        _textureMap.GetAnyValueOrDefault(config.Textures.UV4.MaskTexture)
                    }
                };

                var array = new JArray();
                foreach (var texture in textures) {
                    bool empty = true;
                    foreach (var index in texture) {
                        empty &= index == null;

                        if (index != null) {
                            ExportTexture(index);
                        }
                    }

                    var subArray = new JArray();
                    if (!empty) {
                        foreach (var index in texture) {
                            subArray.Add(PackageIndexToDirPath(index));
                        }
                    }

                    array.Add(subArray);
                }

                if (!obj.ContainsKey(PackageIndexToDirPath(Material)))
                    obj.Add(PackageIndexToDirPath(Material), array);
            }
        }
    }

    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "ConvertToConstant.Global")]
    [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Global")]
    public class Config {
        public string PaksDirectory = "C:\\Program Files\\Epic Games\\Fortnite\\FortniteGame\\Content\\Paks";
        [JsonProperty("UEVersion")]
        public EGame Game = EGame.GAME_UE4_LATEST;
        public List<EncryptionKey> EncryptionKeys = new();
        public bool bDumpAssets = false;
        public int ObjectCacheSize = 100;
        public bool bReadMaterials = true;
        public bool bExportToDDSWhenPossible = true;
        public bool bExportBuildingFoundations = true;
        public string ExportPackage;
        public TextureMapping Textures = new();
    }

    public class TextureMapping {
        public TextureMap UV1 = new() {
            Diffuse = new[] {"Trunk_BaseColor", "Diffuse", "DiffuseTexture", "Base_Color_Tex", "Tex_Color" },
            Normal = new[] {"Trunk_Normal", "Normals", "Normal", "Base_Normal_Tex", "Tex_Normal"},
            Specular = new[] {"Trunk_Specular", "SpecularMasks"},
            Emission = new[] {"EmissiveTexture"},
            MaskTexture = new[] { "MaskTexture" }
        };
        public TextureMap UV2 = new() {
            Diffuse = new[] {"Diffuse_Texture_3"},
            Normal = new[] {"Normals_Texture_3"},
            Specular = new[] {"SpecularMasks_3"},
            Emission = new[] {"EmissiveTexture_3"},
            MaskTexture = new[] { "MaskTexture_3" }
        };
        public TextureMap UV3 = new() {
            Diffuse = new[] {"Diffuse_Texture_4"},
            Normal = new[] {"Normals_Texture_4"},
            Specular = new[] {"SpecularMasks_4"},
            Emission = new[] {"EmissiveTexture_4"},
            MaskTexture = new[] { "MaskTexture_4" }
            };
        public TextureMap UV4 = new() {
            Diffuse = new[] {"Diffuse_Texture_2"},
            Normal = new[] {"Normals_Texture_2"},
            Specular = new[] {"SpecularMasks_2"},
            Emission = new[] {"EmissiveTexture_2"},
            MaskTexture = new[] { "MaskTexture_2" }
            };
    }

    public class TextureMap {
        public string[] Diffuse;
        public string[] Normal;
        public string[] Specular;
        public string[] Emission;
        public string[] MaskTexture;
    }

    public class MainException : Exception {
        public MainException(string message) : base(message) { }
    }
}