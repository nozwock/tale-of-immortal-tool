using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CommandLine;
using CommandLine.Text;

class Program
{
    public static int Main(string[] args)
    {
        var parser = new Parser(config =>
        {
            config.HelpWriter = null;
        });
        var parserResult = parser.ParseArguments<NewModProjectOptions, EncryptOptions, DecryptOptions, RestoreExcelOptions, PackOptions, UnpackOptions, EditOptions>(args);

        return parserResult
            .MapResult(
                (EncryptOptions opts) => RunEncrypt(opts),
                (DecryptOptions opts) => RunDecrypt(opts),
                (RestoreExcelOptions opts) => RunRestoreExcel(opts),
                (PackOptions opts) => RunPack(opts),
                (UnpackOptions opts) => RunUnpack(opts),
                (NewModProjectOptions opts) => RunNewModProject(opts),
                (EditOptions opts) => RunEdit(opts),
                errs =>
                {
                    var helpText = HelpText.AutoBuild(parserResult, h =>
                    {
                        h.AdditionalNewLineAfterOption = false;
                        h.Heading = new HeadingInfo("Mod tooling for Tale of Immortal");
                        h.Copyright = "";
                        return HelpText.DefaultParsingErrorsHandler(parserResult, h);
                    }, e => e);
                    Console.WriteLine(helpText);

                    return 1;
                });
    }

    static int RunEncrypt(EncryptOptions opts)
    {
        foreach (var file in GetFilesByPattern(opts.Folder, @"(\.cache|\.png|\.json)$"))
        {
            var relPath = Path.GetRelativePath(opts.Folder, file);
            if (relPath.StartsWith("ModAssets" + Path.DirectorySeparatorChar))
                continue;

            var data = File.ReadAllBytes(file);
            if (EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.WriteLine($"Encrypting '{file}'");
            var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, encrypted);
        }
        return 0;
    }

    static int RunDecrypt(DecryptOptions opts)
    {
        foreach (var file in Directory.EnumerateFiles(opts.Folder, "*", SearchOption.AllDirectories))
        {
            var data = File.ReadAllBytes(file);
            if (!EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.WriteLine($"Decrypting '{file}'");
            var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, decrypted);
        }
        return 0;
    }

    static int RunRestoreExcel(RestoreExcelOptions opts)
    {
        var root = opts.Folder;

        byte[] data;

        var exportPath = Path.Combine(root, "ModExportData.cache");
        if (File.Exists(exportPath))
        {
            var decrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);

            // Set projectData.excelEncrypt = false
            var json = JsonNode.Parse(decrypted);
            if (json?["projectData"]?["excelEncrypt"] != null && json["projectData"]!["excelEncrypt"]!.GetValue<bool>())
            {
                Console.WriteLine($"Disabling excelEncrypt in '{exportPath}'");
                json["projectData"]!["excelEncrypt"] = false;

                var modified = Encoding.UTF8.GetBytes(json.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
                File.WriteAllBytes(exportPath, EncryptTool.EncryptMult(modified, EncryptTool.modEncryPassword));
            }
            else
            {
                Console.WriteLine($"excelEncrypt is already disabled in '{exportPath}'");
            }
        }

        foreach (var file in GetFilesByPattern(root, @"\.png$"))
        {
            var relPath = Path.GetRelativePath(root, file);
            if (relPath.StartsWith("ModAssets" + Path.DirectorySeparatorChar))
                continue;

            data = File.ReadAllBytes(file);
            if (EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.WriteLine($"Encrypting '{file}'");
            File.WriteAllBytes(file, EncryptTool.EncryptMult(File.ReadAllBytes(file), EncryptTool.modEncryPassword));
        }

        foreach (var file in GetFilesByPattern(root, @"\.json$"))
        {
            var relPath = Path.GetRelativePath(root, file);
            if (relPath.StartsWith("ModAssets" + Path.DirectorySeparatorChar))
                continue;

            data = File.ReadAllBytes(file);
            if (!EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.WriteLine($"Decrypting '{file}'");
            File.WriteAllBytes(file, EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword));
        }
        return 0;
    }

    static int RunPack(PackOptions opts)
    {
        void SetupOutputModFolder(string input, string output, string? soleId)
        {
            // Copy compiled ModMain dll
            if (soleId != null)
            {
                Console.WriteLine($"soleID: {soleId}");
                var modMainDll = Path.Combine(input, "ModCode", "ModMain", "bin", "Release", $"MOD_{soleId}.dll");
                if (File.Exists(modMainDll))
                {
                    var dllOutDir = Path.Combine(output, "ModCode", "dll");
                    var outModMainDll = Path.Combine(dllOutDir, Path.GetFileName(modMainDll));

                    Console.WriteLine($"Copying: '{modMainDll}' -> '{outModMainDll}'");
                    Directory.CreateDirectory(dllOutDir);
                    File.Copy(modMainDll, outModMainDll, true);
                }
            }

            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(output);

            foreach (var srcPath in Directory.EnumerateFileSystemEntries(input, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(input, srcPath);
                var targetPath = Path.Combine(output, relPath);

                // Skip everything under ModCode except dll folder
                if (relPath.StartsWith("ModCode" + Path.DirectorySeparatorChar))
                {
                    var parts = relPath.Split(Path.DirectorySeparatorChar);
                    if (parts.Length < 2 || parts[1] != "dll")
                        continue; // skip non-dll contents
                }

                if (Directory.Exists(srcPath))
                {
                    Directory.CreateDirectory(targetPath);
                }
                else
                {
                    Console.WriteLine($"Copying: '{srcPath}' -> '{targetPath}'");
                    File.Copy(srcPath, targetPath, true);
                }
            }

        }

        var root = opts.Folder;
        var outRoot = opts.OutputFolder ?? root;
        var projPath = Path.Combine(root, "ModProject.cache");
        var exportPath = Path.Combine(root, "ModExportData.cache");

        bool excelEncrypt = false;
        string? soleId = null;
        byte[] exportEncrypted;
        if (File.Exists(projPath))
        {
            var projectNode = JsonNode.Parse(File.ReadAllText(projPath, Encoding.UTF8))!.AsObject();

            if (projectNode.TryGetPropertyValue("excelEncrypt", out var val) && val is JsonValue jv && jv.TryGetValue(out bool b))
                excelEncrypt = b;

            soleId = projectNode["soleID"]?.GetValue<string>();

            var exportRoot = new JsonObject
            {
                ["projectData"] = projectNode,
                ["items"] = new JsonObject(),
                ["modNamespace"] = null
            };

            if (soleId != null) {
                // Important: For dll mods, mod won't be loaded at all if correct namespace is not filled in
                exportRoot["modNamespace"] = $"MOD_{soleId}";
            }

            var exportJsonBytes = Encoding.UTF8.GetBytes(
                exportRoot.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                })
            );
            exportEncrypted = EncryptTool.EncryptMult(exportJsonBytes, EncryptTool.modEncryPassword);
        }
        else if (File.Exists(exportPath))
        {
            var exportDecrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);
            var exportNode = JsonNode.Parse(Encoding.UTF8.GetString(exportDecrypted))!.AsObject();
            if (exportNode["projectData"] is JsonObject proj)
            {
                soleId = proj["soleID"]?.GetValue<string>();
                if (proj.TryGetPropertyValue("excelEncrypt", out var val) && val is JsonValue jv && jv.TryGetValue(out bool b))
                    excelEncrypt = b;
            }
            exportEncrypted = EncryptTool.EncryptMult(exportDecrypted, EncryptTool.modEncryPassword);
        }
        else
        {
            throw new FileNotFoundException("Missing required ModProject.cache in root folder.", projPath);
        }

        Console.WriteLine($"Setting up output folder...\nOutput Folder: '{outRoot}'");
        SetupOutputModFolder(root, outRoot, soleId);
        root = outRoot; // Operating in output folder now
        projPath = Path.Combine(root, "ModProject.cache");
        exportPath = Path.Combine(root, "ModExportData.cache");

        Console.WriteLine("Writing 'ModData.cache'");
        File.WriteAllBytes(exportPath, exportEncrypted);

        var modDataPath = Path.Combine(root, "ModData.cache");
        if (File.Exists(projPath)) File.Delete(projPath);
        if (File.Exists(modDataPath)) File.Delete(modDataPath);

        if (excelEncrypt)
        {
            foreach (var file in GetFilesByPattern(root, @"\.json$"))
            {

                Console.WriteLine($"Encrypting '{file}'");
                var data = File.ReadAllBytes(file);
                var enc = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(file, enc);
            }
        }

        foreach (var file in GetFilesByPattern(opts.Folder, @"(\.png|\.json)$"))
        {
            // Skip ModAssets/
            var relPath = Path.GetRelativePath(root, file);
            if (relPath.StartsWith("ModAssets" + Path.DirectorySeparatorChar))
                continue;

            // Encrypt .json only when excelEncrypt == true, otherwise leave as-is
            if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase) || (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase) && excelEncrypt))
            {
                Console.WriteLine($"Encrypting '{file}'");
                var data = File.ReadAllBytes(file);
                var enc = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(file, enc);
            }
        }

        Console.WriteLine("Pack completed.");
        return 0;
    }

    static int RunUnpack(UnpackOptions opts)
    {
        var root = opts.Folder;
        var exportPath = Path.Combine(root, "ModExportData.cache");

        if (!File.Exists(exportPath))
            throw new FileNotFoundException("Missing required ModExportData.cache in root folder.", exportPath);

        var exportDecrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);
        var exportNode = JsonNode.Parse(Encoding.UTF8.GetString(exportDecrypted))!.AsObject();

        if (exportNode["projectData"] is not JsonObject projectData)
            throw new InvalidDataException("ModExportData.cache missing projectData.");

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var data = File.ReadAllBytes(file);
            if (!EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            // Skip ModExportData.cache metadata file
            if (Path.GetFileName(exportPath).Equals(file, StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine($"Decrypting '{file}'");
            var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, decrypted);
        }

        // Write ModProject.cache
        var projPath = Path.Combine(root, "ModProject.cache");
        File.WriteAllText(projPath,
            projectData.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }),
            Encoding.UTF8);

        // Build ModData.cache (default)
        // TODO: Needs to be updated, like how it's done in NewModProject()
        var modData = new JsonObject();
        var soleId = projectData["soleID"]?.GetValue<string>();
        if (soleId != null)
        {
            modData["modNamespace"] = $"MOD_{soleId}";
        }
        else
        {
            modData["modNamespace"] = null;
        }

        var modDataPath = Path.Combine(root, "ModData.cache");
        File.WriteAllText(modDataPath,
            modData.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }),
            Encoding.UTF8);

        // Delete ModExportData.cache
        File.Delete(exportPath);

        Console.WriteLine("Unpack completed.");
        return 0;
    }

    static int RunNewModProject(NewModProjectOptions opts)
    {
        JsonObject EmptyGroup()
        {
            return new JsonObject
            {
                ["groupName"] = new JsonArray(),
                ["items"] = new JsonArray()
            };
        }

        string GenerateSoleId()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Range(0, 6).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        }

        // Generate IDs
        string soleId = GenerateSoleId();
        int cdGroup = Random.Shared.Next(int.MinValue, int.MaxValue);
        int excelMid = Random.Shared.Next(int.MinValue, int.MaxValue);
        long createTicks = DateTime.UtcNow.Ticks;

        string root = $"Mod_{soleId} {opts.Name}";
        if (Directory.Exists(root))
        {
            Console.Error.WriteLine($"Error: Folder '{root}' already exists.");
            return 1;
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "ModAssets"));
        Directory.CreateDirectory(Path.Combine(root, "ModCode", "ModMain"));
        Directory.CreateDirectory(Path.Combine(root, "ModExcel"));


        // ModData.cache
        var modData = new JsonObject
        {
            ["roleCreateFeature"] = EmptyGroup(),
            ["fortuitousEvent"] = new JsonObject
            {
                ["cdGroup"] = new JsonObject { ["value"] = cdGroup },
                ["groupName"] = new JsonArray(),
                ["items"] = new JsonArray()
            },
            ["npcCondition"] = EmptyGroup(),
            ["mapPosition"] = EmptyGroup(),
            ["worldFortuitousEventBase"] = EmptyGroup(),
            ["itemProps"] = EmptyGroup(),
            ["taskBase"] = EmptyGroup(),
            ["dramaDialogue"] = EmptyGroup(),
            ["dramaNpc"] = EmptyGroup(),
            ["npcAddWorld"] = EmptyGroup(),
            ["schoolSmall"] = EmptyGroup(),
            ["schoolInitScale"] = EmptyGroup(),
            ["schoolCustom"] = EmptyGroup(),
            ["horseModel"] = EmptyGroup(),
            ["dungeonBase"] = EmptyGroup(),
            ["excelMID"] = excelMid,
            ["modNamespace"] = $"MOD_{soleId}"
        };

        File.WriteAllText(Path.Combine(root, "ModData.cache"),
            JsonSerializer.Serialize(modData, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

        // ModProject.cache
        var modProject = new JsonObject
        {
            ["soleID"] = soleId,
            ["createTicks"] = createTicks,
            ["name"] = $"Mod {soleId} {opts.Name}",
            ["author"] = "gse orca",
            ["desc"] = $"Creation Time {DateTime.Now:yyyy_MM_dd_HH_mm_ss}",
            ["ver"] = "v 1.0.0",
            ["autoSave"] = true,
            ["isCreateNPC"] = true,
            ["exportVer"] = 0,
            ["accountID"] = 0,
            ["publishedFileID"] = 0,
            ["visibleState"] = 0,
            ["openCode"] = 0,
            ["curUpdateDesc"] = null,
            ["tags"] = new JsonArray(),
            ["addPreviewPaths"] = new JsonArray(),
            ["excelEncrypt"] = false
        };

        File.WriteAllText(Path.Combine(root, "ModProject.cache"),
            JsonSerializer.Serialize(modProject, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

        Console.WriteLine($"New mod template created at '{root}'");
        return 0;
    }
    static int RunEdit(EditOptions opts)
    {
        static string GetEditor()
        {
            string? editor = Environment.GetEnvironmentVariable("VISUAL")
                          ?? Environment.GetEnvironmentVariable("EDITOR");

            if (!string.IsNullOrEmpty(editor))
                return editor;

            // OS defaults
            if (OperatingSystem.IsWindows())
                return "notepad";
            if (OperatingSystem.IsMacOS())
                return "open"; // macOS: open -t file
            return "vi"; // Linux default
        }

        if (!File.Exists(opts.InputFile))
        {
            Console.Error.WriteLine("File not found: " + opts.InputFile);
            return 1;
        }

        byte[] data = File.ReadAllBytes(opts.InputFile);
        if (!EncryptTool.LooksEncrypted(data))
        {
            Console.Error.WriteLine("File is already decrypted: " + opts.InputFile);
            return 1;
        }

        byte[] decryptedData = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
        string decryptedText = Encoding.UTF8.GetString(decryptedData);

        string formattedJson;
        try
        {
            var doc = JsonDocument.Parse(decryptedText);
            formattedJson = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("File is not valid JSON: " + ex.Message);
            return 1;
        }

        string tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, formattedJson);

        try
        {
            string editor = GetEditor();

            var psi = new ProcessStartInfo
            {
                FileName = editor,
                UseShellExecute = false
            };
            if (OperatingSystem.IsMacOS())
            {
                psi.ArgumentList.Add("-t");
            }
            psi.ArgumentList.Add(tempFile);

            using var proc = Process.Start(psi)!;
            proc.WaitForExit();

            var modifiedData = File.ReadAllBytes(tempFile);
            string modifiedText = Encoding.UTF8.GetString(modifiedData);

            if (formattedJson == modifiedText)
            {
                Console.Error.WriteLine("Nothing to update.");
                return 0;
            }

            // Validate JSON
            try
            {
                JsonDocument.Parse(modifiedText);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Invalid JSON after edit: " + ex.Message);
                return 1;
            }

            // Encrypt and overwrite
            File.WriteAllBytes(opts.InputFile, EncryptTool.EncryptMult(modifiedData, EncryptTool.modEncryPassword));

            Console.WriteLine($"Updated file '{opts.InputFile}'");
            return 0;
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    public static IEnumerable<string> GetFilesByPattern(string path,
                       string rePattern = "",
                       SearchOption searchOption = SearchOption.AllDirectories)
    {
        Regex re = new Regex(rePattern, RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(path, "*", searchOption)
                        .Where(file =>
                                 re.IsMatch(Path.GetFileName(file)));
    }
}

[Verb("encrypt", HelpText = "Encrypt all mod files in a folder (in-place).")]
class EncryptOptions
{
    [Value(0, MetaName = "folder", Required = true, HelpText = "Folder containing mod files.")]
    public string Folder { get; set; } = "";
}

[Verb("decrypt", HelpText = "Decrypt all files in a folder (in-place).")]
class DecryptOptions
{
    [Value(0, MetaName = "folder", Required = true, HelpText = "Folder containing mod files.")]
    public string Folder { get; set; } = "";
}

[Verb("restore-excel", HelpText = "Decrypt json mod files (in-place), and for ModExportData.cache modify excelEncrypt=false.")]
class RestoreExcelOptions
{
    [Value(0, MetaName = "folder", Required = true, HelpText = "Folder containing mod files.")]
    public string Folder { get; set; } = "";
}

[Verb("pack", HelpText = "Pack mod files (in-place), so that the mod can be loaded in-game.")]
class PackOptions
{
    [Value(0, MetaName = "folder", Required = true, HelpText = "Folder containing mod files.")]
    public string Folder { get; set; } = "";

    [Option('o', "output", Required = false, HelpText = "Output folder. Defaults to input folder.")]
    public string? OutputFolder { get; set; }
}

[Verb("unpack", HelpText = "Unpack mod files (in-place), so that the mod can be edited in the in-game mod editor.")]
class UnpackOptions
{
    [Value(0, MetaName = "folder", Required = true, HelpText = "Folder containing mod files.")]
    public string Folder { get; set; } = "";
}

[Verb("new", HelpText = "Create a new mod template folder.")]
public class NewModProjectOptions
{
    [Value(0, MetaName = "name", Required = true, HelpText = "Name of the mod (and folder).")]
    public string Name { get; set; } = default!;
}

[Verb("edit", HelpText = "Edit encrypted file in default text editor (Currently restricted to json files). Useful for editing 'ModExportData.cache'.")]
class EditOptions
{
    [Value(0, Required = true, HelpText = "Path to the file.")]
    public string InputFile { get; set; } = null!;
}
