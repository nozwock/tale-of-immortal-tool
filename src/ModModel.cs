using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace TaleOfImmortalTool;

class AotJsonBase<T> where T : IAotJson<T>
{
    public static T Parse(ReadOnlySpan<char> json)
        => JsonSerializer.Deserialize(json, T.GetJsonTypeInfo())
            ?? throw new InvalidOperationException($"Failed deserialization to {typeof(T).Name}.");
    public static T Parse(JsonNode json)
        => JsonSerializer.Deserialize(json, T.GetJsonTypeInfo())
            ?? throw new InvalidOperationException($"Failed deserialization to {typeof(T).Name}.");

    public string ToJsonString(JsonSerializerOptions? options)
        => JsonSerializer.Serialize(this, T.GetJsonTypeInfo(options));
    public JsonNode? ToJsonNode() => JsonSerializer.SerializeToNode(this, T.GetJsonTypeInfo());
}

interface IAotJson<T> where T : IAotJson<T>
{
    // https://github.com/dotnet/runtime/issues/94135#issuecomment-2102577063
    public static abstract JsonTypeInfo<T> GetJsonTypeInfo(JsonSerializerOptions? options = null);
}

class ProjectData : AotJsonBase<ProjectData>, IAotJson<ProjectData>
{
    public string SoleID { get; set; }
    public long CreateTicks { get; set; }
    public string Name { get; set; }
    public string Author { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Ver { get; set; } = "1.0.0";
    public bool AutoSave { get; set; } = true;
    public bool IsCreateNPC { get; set; } = true;
    public int ExportVer { get; set; }
    public uint AccountID { get; set; }
    public ulong PublishedFileID { get; set; }
    public int VisibleState { get; set; }
    public int OpenCode { get; set; }
    public string CurUpdateDesc { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> AddPreviewPaths { get; set; } = [];
    public bool ExcelEncrypt { get; set; } = false;

    public ProjectData(string name, string? soleId = null)
    {
        if (soleId is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(soleId);

        Name = name;
        SoleID = soleId ?? GenerateSoleId();
        CreateTicks = DateTime.UtcNow.Ticks;
    }

    public static string GenerateSoleId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string([.. Enumerable.Range(0, 6).Select(_ => chars[Random.Shared.Next(chars.Length)])]);
    }

    public static JsonTypeInfo<ProjectData> GetJsonTypeInfo(JsonSerializerOptions? options = null)
    {
        options ??= new();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        return new SourceGenerationContext(options).ProjectData;
    }
}

class ModInfo
{
    public enum InfoCollectOption
    {
        Any,
        ProjectData,
        ExportData,
    }

    private readonly JsonObject modExportData;
    public ProjectData ProjectData { get; set; }
    public string? ModNamespace
    {
        get => modExportData?["modNamespace"]?.GetValue<string>();
        set => modExportData?["modNamespace"] = value;
    }

    /// <exception cref="FileNotFoundException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public ModInfo(string modFolder, InfoCollectOption infoCollect = InfoCollectOption.Any)
    {
        var root = modFolder;
        var projectDataPath = Path.Combine(root, "ModProject.cache");
        var modDataPath = Path.Combine(root, "ModData.cache");
        var exportDataPath = Path.Combine(root, "ModExportData.cache");

        if ((infoCollect == InfoCollectOption.Any
            || infoCollect == InfoCollectOption.ProjectData)
            && File.Exists(projectDataPath))
        {
            if (!File.Exists(modDataPath))
            {
                throw new FileNotFoundException(
                    $"Found \"{projectDataPath}\" but cannot find the associated \"{modDataPath}\"");
            }

            ProjectData = ProjectData.Parse(File.ReadAllText(projectDataPath, Encoding.UTF8));

            modExportData = new JsonObject
            {
                ["projectData"] = ProjectData.ToJsonNode(),
                // TODO: Doesn't support packing ModData, only modNamespace is read from it currently
                ["items"] = new JsonObject(),
                ["modNamespace"] = null
            };
        }
        else if ((infoCollect == InfoCollectOption.Any
            || infoCollect == InfoCollectOption.ExportData)
            && File.Exists(exportDataPath))
        {
            var exportDecrypted = EncryptTool.DecryptMult(
                File.ReadAllBytes(exportDataPath),
                EncryptTool.modEncryPassword);
            modExportData = JsonNode.Parse(Encoding.UTF8.GetString(exportDecrypted))!.AsObject();
            if (modExportData["projectData"] is JsonObject projectData)
            {
                ProjectData = ProjectData.Parse(projectData);
            }
            else
            {
                throw new InvalidOperationException("Failed to find .projectData object in ModExportData.cache");
            }
        }
        else
        {
            string missingProjectFiles = "ModProject.cache, ModData.cache";
            var missingFiles = infoCollect switch
            {
                InfoCollectOption.Any => $"({missingProjectFiles}) | ModExportData.cache",
                InfoCollectOption.ProjectData => missingProjectFiles,
                InfoCollectOption.ExportData => "ModExportData.cache",
                _ => throw new NotImplementedException(),
            };
            throw new FileNotFoundException(
                $"Failed to collect mod info: missing {missingFiles} in folder: \"{projectDataPath}\"");
        }

        var soleId = modExportData["projectData"]?["soleID"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(soleId))
        {
            throw new InvalidOperationException("Cannot find soleID in mod's metadata.");
        }

        if (File.Exists(modDataPath))
        {
            // Important: For dll mods, mod won't be loaded at all if correct namespace is not filled in
            var modDataRoot = JsonNode.Parse(File.ReadAllText(modDataPath, Encoding.UTF8))!.AsObject();
            ModNamespace = modExportData["modNamespace"]?.GetValue<string?>()
                ?? modDataRoot["modNamespace"]?.GetValue<string?>()
                ?? $"MOD_{soleId}";
        }
    }

    public JsonObject AsExportData()
    {
        modExportData["projectData"] = ProjectData.ToJsonNode()!.AsObject();
        return modExportData;
    }
}