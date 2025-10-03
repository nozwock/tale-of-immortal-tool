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
using ICSharpCode.SharpZipLib.GZip;

class Program
{
    public static int Main(string[] args)
    {
        var parser = new Parser(config =>
        {
            config.HelpWriter = null;
        });
        var parserResult = parser.ParseArguments<
            NewModProjectOptions,
            EncryptOptions,
            DecryptOptions,
            RestoreExcelOptions,
            PackOptions,
            UnpackOptions,
            EditOptions,
            SaveUnpackOptions,
            SavePackOptions
        >(args);

        return parserResult
            .MapResult(
                (EncryptOptions opts) => RunEncrypt(opts),
                (DecryptOptions opts) => RunDecrypt(opts),
                (SaveUnpackOptions opts) => RunSaveUnpack(opts),
                (SavePackOptions opts) => RunSavePack(opts),
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
        if (Directory.Exists(opts.Path))
        {
            foreach (var file in Directory.EnumerateFiles(opts.Path, "*", SearchOption.AllDirectories))
            {
                var data = File.ReadAllBytes(file);
                if (EncryptTool.LooksEncrypted(data))
                {
                    continue;
                }

                Console.WriteLine($"Encrypting '{file}'");
                var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(file, encrypted);
            }
        }
        else if (File.Exists(opts.Path))
        {
            var file = opts.Path;
            var data = File.ReadAllBytes(file);
            if (EncryptTool.LooksEncrypted(data))
            {
                return 0;
            }

            Console.WriteLine($"Encrypting '{file}'");
            var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, encrypted);
        }
        else
        {
            Console.WriteLine($"Neither a file nor directory: '{opts.Path}'");
            return 1;
        }
        return 0;
    }

    static int RunDecrypt(DecryptOptions opts)
    {
        if (Directory.Exists(opts.Path))
        {
            foreach (var file in Directory.EnumerateFiles(opts.Path, "*", SearchOption.AllDirectories))
            {
                var data = File.ReadAllBytes(file);
                if (!EncryptTool.LooksEncrypted(data))
                {
                    continue;
                }

                Console.Error.WriteLine($"Decrypting '{file}'");
                var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(file, decrypted);
            }
        }
        else if (File.Exists(opts.Path))
        {
            var file = opts.Path;
            var data = File.ReadAllBytes(file);
            if (!EncryptTool.LooksEncrypted(data))
            {
                return 0;
            }

            Console.Error.WriteLine($"Decrypting '{file}'");
            var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
            if (opts.Inplace)
            {
                File.WriteAllBytes(file, decrypted);
            }
            else
            {
                Console.OpenStandardOutput().Write(decrypted);
            }
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: '{opts.Path}'");
            return 1;
        }
        return 0;
    }

    const string SAVE_UNPACK_META_FILENAME = "unpack_metadata.json";

    // For PC, on Android it's gzipped BinaryFormatter serialized objects instead
    static int RunSaveUnpack(SaveUnpackOptions opts)
    {
        var metadata = new SaveUnpackMetadata();

        byte[] Decompress(byte[] input)
        {
            using var stream = new MemoryStream(input);
            using var gzip = new GZipInputStream(stream);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        void ProcessFile(string filePath)
        {
            var rootPath = Path.GetDirectoryName(filePath)!;
            var filename = Path.GetFileName(filePath);
            var outputBasename = Path.GetFileNameWithoutExtension(filename);
            var ext = Path.GetExtension(filename);

            var meta = new SaveUnpackMetadata.FileMeta();

            var bytes = File.ReadAllBytes(filePath);
            if (EncryptTool.LooksEncrypted(bytes))
            {
                meta.IsFileEncrypted = true;
                try
                {
                    var decryptedName = EncryptTool.DecryptDES(outputBasename, EncryptTool.cacheEncryPassword);
                    meta.IsOriginalNameEncrypted = true;
                    if (!opts.KeepNames)
                    {
                        meta.IsNameDecrypted = true;
                        outputBasename = decryptedName;
                    }
                }
                catch (FormatException) // Invalid base64, etc
                { }

                var decrypted = EncryptTool.DecryptMult(bytes, EncryptTool.cacheEncryPassword);
                var decompressed = Decompress(decrypted);

                try
                {
                    using var doc = JsonDocument.Parse(decompressed);
                    decompressed = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }
                    ));
                }
                catch { }

                // In-place
                var outFile = Path.Combine(rootPath, outputBasename + ext);
                Console.Error.WriteLine($"`{filePath}` >> `{outFile}`");

                File.Delete(filePath);
                File.WriteAllBytes(outFile, decompressed);

                metadata.Files[Path.GetRelativePath(opts.Path, outFile)] = meta;
            }
            else
            {
                Console.Error.WriteLine($"`{filePath}` isn't encrypted, skipped.");
                metadata.Files[Path.GetRelativePath(opts.Path, filePath)] = meta;
            }
        }

        if (Directory.Exists(opts.Path))
        {
            foreach (var file in Directory.EnumerateFiles(opts.Path, "*", SearchOption.AllDirectories))
            {
                ProcessFile(file);
            }

            var metaPath = Path.Combine(opts.Path, SAVE_UNPACK_META_FILENAME);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (File.Exists(opts.Path))
        {
            ProcessFile(opts.Path);
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: `{opts.Path}`");
            return 1;
        }

        return 0;
    }

    static int RunSavePack(SavePackOptions opts)
    {
        byte[] Compress(byte[] input)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipOutputStream(output))
            {
                gzip.IsStreamOwner = false;
                gzip.Write(input, 0, input.Length);
            }
            return output.ToArray();
        }

        void ProcessFile(string filePath, SaveUnpackMetadata.FileMeta? meta = null)
        {
            if (!File.Exists(filePath) || meta?.IsFileEncrypted == false)
            {
                return;
            }

            var rootPath = Path.GetDirectoryName(filePath)!;
            var outputBasename = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath);

            var bytes = File.ReadAllBytes(filePath);
            // Minify json before packing
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc.RootElement));
            }
            catch { }

            var compressed = Compress(bytes);
            var encrypted = EncryptTool.EncryptMult(compressed, EncryptTool.cacheEncryPassword);

            if (!opts.KeepNames && (meta == null || (meta.IsOriginalNameEncrypted && meta.IsNameDecrypted)))
            {
                outputBasename = EncryptTool.EncryptDES(outputBasename, EncryptTool.cacheEncryPassword);
            }

            var outFile = Path.Combine(rootPath, outputBasename + ext);
            Console.Error.WriteLine($"`{filePath}` >> `{outFile}`");

            File.Delete(filePath);
            File.WriteAllBytes(outFile, encrypted);
        }

        if (Directory.Exists(opts.Path))
        {
            var metaPath = Path.Combine(opts.Path, SAVE_UNPACK_META_FILENAME);
            if (!File.Exists(metaPath))
            {
                Console.Error.WriteLine("Metadata file not found. Run save-unpack first.");
                return 1;
            }
            var metadata = JsonSerializer.Deserialize<SaveUnpackMetadata>(File.ReadAllText(metaPath))!;

            foreach (var kv in metadata.Files)
            {
                var relPath = kv.Key;
                var meta = kv.Value;
                var filePath = Path.Combine(opts.Path, relPath);

                ProcessFile(filePath, meta);
            }

            Console.Error.WriteLine("Pack complete, removing metadata file...");
            File.Delete(metaPath);
        }
        else if (File.Exists(opts.Path))
        {
            ProcessFile(opts.Path);
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: `{opts.Path}`");
            return 1;
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
            if (relPath.StartsWith("ModAssets") || relPath.StartsWith("ModCode") || relPath.StartsWith("ModExcel"))
                continue;

            data = File.ReadAllBytes(file);
            if (EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.WriteLine($"Encrypting '{file}'");
            File.WriteAllBytes(file, EncryptTool.EncryptMult(File.ReadAllBytes(file), EncryptTool.modEncryPassword));
        }

        // Decrypt any encrypted json file, even if outside ModExcel
        foreach (var file in GetFilesByPattern(root, @"\.json$"))
        {
            var relPath = Path.GetRelativePath(root, file);

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
        void SetupOutputModFolder(string input, string output, string? modNamespace)
        {
            // Copy compiled ModCode Release artifacts
            if (modNamespace != null)
            {
                var releaseDir = Path.Combine(input, "ModCode", "ModMain", "bin", "Release");
                var modMainDll = Path.Combine(releaseDir, $"{modNamespace}.dll");

                Console.WriteLine($"modNamespace: {modNamespace}");
                if (File.Exists(modMainDll))
                {
                    // Copy over all files from `ModCode/ModMain/bin/Release` if main dll exists
                    // This is the behaviour of the Game's in-game mod tooling as well.

                    var dllOutDir = Path.Combine(output, "ModCode", "dll");
                    Directory.CreateDirectory(dllOutDir);

                    foreach (var file in Directory.EnumerateFiles(releaseDir))
                    {
                        var ext = Path.GetExtension(file);
                        if (string.Equals(ext, ".pdb", StringComparison.OrdinalIgnoreCase))
                            continue; // skip debug symbols

                        var dest = Path.Combine(dllOutDir, Path.GetFileName(file));
                        Console.Error.WriteLine($"Copying: '{file}' -> '{dest}'");
                        File.Copy(file, dest, overwrite: true);
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Couldn't find '{modNamespace}.dll', skipping ModCode");
                }
            }
            else
            {
                Console.Error.WriteLine("modNamespace not found, skipping ModCode");
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
        JsonObject exportRoot;
        if (File.Exists(projPath))
        {
            var projectNode = JsonNode.Parse(File.ReadAllText(projPath, Encoding.UTF8))!.AsObject();

            if (projectNode.TryGetPropertyValue("excelEncrypt", out var val) && val is JsonValue jv && jv.TryGetValue(out bool b))
                excelEncrypt = b;

            soleId = projectNode["soleID"]?.GetValue<string>();

            exportRoot = new JsonObject
            {
                ["projectData"] = projectNode,
                // TODO: Doesn't support packing ModData, only modNamespace is read from it currently
                ["items"] = new JsonObject(),
                ["modNamespace"] = null
            };
        }
        else if (File.Exists(exportPath))
        {
            var exportDecrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);
            exportRoot = JsonNode.Parse(Encoding.UTF8.GetString(exportDecrypted))!.AsObject();
            if (exportRoot["projectData"] is JsonObject projectNode)
            {
                soleId = projectNode["soleID"]?.GetValue<string>();
                if (projectNode.TryGetPropertyValue("excelEncrypt", out var val) && val is JsonValue jv && jv.TryGetValue(out bool b))
                    excelEncrypt = b;
            }
        }
        else
        {
            throw new FileNotFoundException("Missing required ModProject.cache in root folder.", projPath);
        }

        var modDataPath = Path.Combine(root, "ModData.cache");
        if (File.Exists(modDataPath))
        {
            var modDataRoot = JsonNode.Parse(File.ReadAllText(modDataPath, Encoding.UTF8))!.AsObject();
            // Important: For dll mods, mod won't be loaded at all if correct namespace is not filled in
            exportRoot["modNamespace"] = exportRoot["modNamespace"]?.GetValue<string?>() ?? modDataRoot["modNamespace"]?.GetValue<string?>();
        }
        exportRoot["modNamespace"] = exportRoot["modNamespace"]?.GetValue<string?>() ?? (soleId != null ? $"MOD_{soleId}" : null);

        Console.WriteLine($"Setting up output folder...\nOutput Folder: '{outRoot}'");
        var modNamespace = exportRoot["modNamespace"]?.GetValue<string?>();
        SetupOutputModFolder(root, outRoot, modNamespace);
        root = outRoot; // Operating in output folder now
        projPath = Path.Combine(root, "ModProject.cache");
        exportPath = Path.Combine(root, "ModExportData.cache");

        Console.WriteLine("Writing 'ModExportData.cache'");

        var exportJsonBytes = Encoding.UTF8.GetBytes(
            exportRoot.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            })
        );
        var exportEncrypted = EncryptTool.EncryptMult(exportJsonBytes, EncryptTool.modEncryPassword);
        File.WriteAllBytes(exportPath, exportEncrypted);

        if (File.Exists(projPath)) File.Delete(projPath);
        if (File.Exists(modDataPath)) File.Delete(modDataPath);

        var modExcelDir = Path.Combine(root, "ModExcel");
        if (Directory.Exists(modExcelDir))
        {
            foreach (var file in GetFilesByPattern(modExcelDir, @"\.json$"))
            {
                var data = File.ReadAllBytes(file);
                if (excelEncrypt && EncryptTool.LooksEncrypted(data) || !excelEncrypt && !EncryptTool.LooksEncrypted(data))
                {
                    continue;
                }

                byte[] bytes;
                if (excelEncrypt)
                {
                    Console.WriteLine($"Encrypting '{file}'");
                    bytes = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                }
                else
                {
                    Console.WriteLine($"Decrypting '{file}'");
                    bytes = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
                }
                File.WriteAllBytes(file, bytes);
            }
        }

        foreach (var file in GetFilesByPattern(root, @"(\.png)$"))
        {
            // Skip ModAssets/ and ModCode/
            var relPath = Path.GetRelativePath(root, file);
            if (relPath.StartsWith("ModAssets" + Path.DirectorySeparatorChar) | relPath.StartsWith("ModCode" + Path.DirectorySeparatorChar))
                continue;

            var data = File.ReadAllBytes(file);
            if (EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.WriteLine($"Encrypting '{file}'");
            var enc = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, enc);
        }

        Console.WriteLine("Pack completed.");
        return 0;
    }

    static int RunUnpack(UnpackOptions opts)
    {
        // TODO: Add an option for separate output folder
        var root = opts.Folder;
        var exportPath = Path.Combine(root, "ModExportData.cache");

        if (!File.Exists(exportPath))
            throw new FileNotFoundException("Missing required ModExportData.cache in root folder.", exportPath);

        var exportDecrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);
        var exportNode = JsonNode.Parse(Encoding.UTF8.GetString(exportDecrypted))!.AsObject();

        if (exportNode["projectData"] is not JsonObject projectData)
            throw new InvalidDataException("ModExportData.cache missing projectData.");

        // Decrypt all the files if encrypted
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

        if (exportNode["items"] is JsonObject items)
        {
            var jsonDir = Path.Combine(root, "ModExcel", "ModExportDataJson");
            Directory.CreateDirectory(jsonDir);

            foreach (var (filename, data) in items)
            {
                var filePath = Path.Combine(jsonDir, filename + ".json");
                Console.Error.WriteLine($"Unpacking ModExportData's {filename} to `{filePath}`");
                File.WriteAllText(filePath,
                    data.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }),
                    Encoding.UTF8
                );
            }
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

        var soleId = projectData["soleID"]?.GetValue<string>();
        var modData = NewModDataCache(soleId);
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
        string GenerateSoleId()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Range(0, 6).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        }

        // Generate IDs
        string soleId = GenerateSoleId();
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
        var modData = NewModDataCache(soleId);

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

    static JsonObject NewModDataCache(string? soleId)
    {
        JsonObject EmptyGroup()
        {
            return new JsonObject
            {
                ["groupName"] = new JsonArray(),
                ["items"] = new JsonArray()
            };
        }

        int cdGroup = Random.Shared.Next(int.MinValue, int.MaxValue);
        int excelMid = Random.Shared.Next(int.MinValue, int.MaxValue);

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
            ["modNamespace"] = soleId != null ? $"MOD_{soleId}" : null
        };

        return modData;
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

public class SaveUnpackMetadata
{
    public Dictionary<string, FileMeta> Files { get; set; } = new();

    public class FileMeta
    {
        public bool IsFileEncrypted { get; set; }
        public bool IsOriginalNameEncrypted { get; set; }
        public bool IsNameDecrypted { get; set; }
    }
}


[Verb("encrypt", HelpText = "Encrypt a single file or all files in a folder (in-place).")]
class EncryptOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "File or folder containing files.")]
    public string Path { get; set; } = "";
}

[Verb("decrypt", HelpText = "Decrypt a single file or all files in a folder (in-place).")]
class DecryptOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "File or folder containing files.")]
    public string Path { get; set; } = "";

    [Option('i', "in-place", Required = false, HelpText = "Decrypt file in place. Output to stdout otherwise.")]
    public bool Inplace { get; set; } = false;
}

[Verb("restore-excel", HelpText = "Decrypt json mod files (in-place), and for ModExportData.cache modify excelEncrypt=false.")]
class RestoreExcelOptions
{
    [Value(0, MetaName = "folder", Required = true, HelpText = "Folder containing mod files.")]
    public string Folder { get; set; } = "";
}

[Verb("pack", HelpText = "Pack mod files, so that the mod can be loaded in-game.\nDlls and related assets from 'ModCode/ModMain/bin/Release/' gets copied over to 'ModCode/dll/'.")]
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

[Verb("save-pack", HelpText = "Compress and encrypt a single save file or all save files in a folder (in-place).")]
class SavePackOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "Save file or folder containing save files.")]
    public string Path { get; set; } = "";

    [Option('k', "keep-names", Required = false, HelpText = "Keep original filenames, do not encrypt them.")]
    public bool KeepNames { get; set; } = false;
}

[Verb("save-unpack", HelpText = "Decompress and decrypt a single save file or all save files in a folder (in-place).")]
class SaveUnpackOptions
{
    [Value(0, MetaName = "path", Required = true, HelpText = "Save file or folder containing save files.")]
    public string Path { get; set; } = "";

    [Option('k', "keep-names", Required = false, HelpText = "Keep original filenames, do not decrypt them.")]
    public bool KeepNames { get; set; } = false;
}