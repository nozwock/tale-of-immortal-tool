using System.Buffers;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.GZip;

namespace TaleOfImmortalTool;

partial class Program
{
    const string packOutputNameTemplate = "Mod_{{SoleId}}_{{FolderName}}";

    static readonly JsonSerializerOptions jsonPrettySerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    static readonly JsonWriterOptions jsonPrettyWriterOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static int Main(string[] args)
    {
        var argPath = new Argument<FileSystemInfo>("path")
        {
            Description = "File or folder containing files.",
        }.AcceptExistingOnly();
        var argModFolder = new Argument<DirectoryInfo>("folder")
        {
            Description = "Folder containing mod files.",
        }.AcceptExistingOnly();

        var optCleanOutput = new Option<bool>("--clean")
        {
            Description = "Recursively delete output directory if it exists before writing anything to it.",
        };

        var optIgnoreGlobs = new Option<List<string>>("--glob")
        {
            Description = "Ignore globset, gitignore style.",
            Aliases = { "-g" },
        };
        var optIgnoreFiles = new Option<List<FileInfo>>("--ignore-file")
        {
            Description = "Path to additional ignore files. Higher priortiy than default ignore files.",
        }.AcceptExistingOnly();
        var optNoIgnoreFile = new Option<bool>("--no-ignore")
        {
            Description = "Don't ignore files based on the default .ignore and .gitignore exclude patterns.",
        };

        var argModName = new Argument<string>("name")
        {
            Description = "Name of the mod (and folder).",
        }.AcceptLegalFileNamesOnly();
        var cmdNewMod = new Command(
            "new",
            "Create a new mod template folder."
        )
        {
            argModName
        };
        cmdNewMod.SetAction(parsed =>
            RunNewModProject(parsed.GetValue(argModName)!));

        var argInputFile = new Argument<FileInfo>("file")
        {
            Description = "Path to the file.",
        }.AcceptExistingOnly();
        var cmdEdit = new Command(
            "edit",
            "Edit encrypted file in default text editor (currently restricted to json files)."
            + $" Useful for editing '{ModInfo.exportDataFileName}'."
        )
        {
            argInputFile
        };
        cmdEdit.SetAction(parsed =>
            RunEdit(parsed.GetValue(argInputFile)!));

        var cmdEncrypt = new Command("encrypt", "Encrypt a single file or all files in a folder (in-place).")
        {
            argPath
        };
        cmdEncrypt.SetAction(parsed =>
            RunEncrypt(parsed.GetValue(argPath)!));

        var optDecryptInplace = new Option<bool>("--in-place")
        {
            Description = """
                Decrypt file in place. Output to stdout otherwise.
                Only applies when the `path` is a file. For folders, it's always in place.
                """,
            Aliases = { "-i" },
        };
        var cmdDecrypt = new Command("decrypt", "Decrypt a single file or all files in a folder (in-place).")
        {
            argPath,
            optDecryptInplace,
        };
        cmdDecrypt.SetAction(parsed =>
            RunDecrypt(
                parsed.GetValue(argPath)!,
                parsed.GetValue(optDecryptInplace)));

        var cmdModRestoreExcel = new Command(
            "restore",
            $"Decrypt .json mod files (in-place), and set `excelEncrypt=false` in `{ModInfo.exportDataFileName}`."
        )
        {
            argModFolder
        };
        cmdModRestoreExcel.SetAction(parsed =>
            RunModRestoreExcel(parsed.GetValue(argModFolder)!));

        var optModPackOutput = new Option<DirectoryInfo>("--output")
        {
            Description = "Output folder. Defaults to input folder.",
            Aliases = { "-o" },
        }.AcceptLegalFilePathsOnly();
        var optModPackOutputFormat = new Option<string>("--output-format")
        {
            Description = """
            Output folder name format. Only used when --output is specified.
            Set it to empty string to disable formatting.
            """,
            DefaultValueFactory = _ => packOutputNameTemplate,
        };
        var optModPackUseReadme = new Option<FileInfo>("--readme")
        {
            Description = "Use a markdown file for description string in the mod's cooked metadata.\n"
            + "Uses README.md from input folder by default if `desc` field is either empty or "
            + $"doesn't exist in `{ModInfo.projectDataFileName}` or `{ModInfo.exportDataFileName}`.",
        }.AcceptExistingOnly();
        var cmdModPack = new Command(
            "pack",
            """
            Pack mod files, so that the mod can be loaded in-game.
            Dlls and related assets from 'ModCode/ModMain/bin/Release/' gets copied over to 'ModCode/dll/'.
            """
        )
        {
            argModFolder,
            optModPackOutput,
            optCleanOutput,
            optIgnoreGlobs,
            optIgnoreFiles,
            optNoIgnoreFile,
            optModPackOutputFormat,
            optModPackUseReadme,
        };
        cmdModPack.SetAction(parsed =>
            RunModPack(
                folder: parsed.GetValue(argModFolder)!,
                outputFolder: parsed.GetValue(optModPackOutput),
                outputFormat: parsed.GetValue(optModPackOutputFormat)!,
                ignoreGlobs: ExtendGlobsWithIgnoreFiles(
                    // CommandLine.Option<List<T>> gives List<T> instead of null if option wasn't specified
                    parsed.GetValue(optIgnoreGlobs)!,
                    parsed.GetValue(optIgnoreFiles)!),
                noIgnoreFiles: parsed.GetValue(optNoIgnoreFile),
                cleanOutput: parsed.GetValue(optCleanOutput),
                readmeFile: parsed.GetValue(optModPackUseReadme)));

        var cmdModUnpack = new Command(
            "unpack",
            "Unpack mod files (in-place), decrypting files and extracting "
            + $"{ModInfo.projectDataFileName} from {ModInfo.exportDataFileName}."
        )
        {
            argModFolder
        };
        cmdModUnpack.SetAction(parsed =>
            RunModUnpack(parsed.GetValue(argModFolder)!));

        var cmdMod = new Command(
            "mod",
            "Mod files packing and unpacking."
            + $"\nNOTE: Mod made using the in-game mod creator aren't supported well currently when building "
            + $"{ModInfo.exportDataFileName}, as {ModInfo.modDataFileName} is largely ignored except for the "
            + "`.modNamespace` field."
        )
        {
            Subcommands =
            {
                cmdModRestoreExcel,
                cmdModPack,
                cmdModUnpack,
            },
        };

        var argSavePath = new Argument<FileSystemInfo>("path")
        {
            Description = "Save file or folder containing save files.",
        }.AcceptExistingOnly();
        var optNoEncryptFilenames = new Option<bool>("--keep-names")
        {
            Description = "Keep original filenames, do not encrypt them.",
            Aliases = { "-k" },
        };
        var cmdSavePack = new Command(
            "pack",
            "Compress and encrypt a single save file or all save files in a folder (in-place)."
        )
        {
            argSavePath,
            optNoEncryptFilenames
        };
        cmdSavePack.SetAction(parsed =>
            RunSavePack(
                parsed.GetValue(argSavePath)!,
                parsed.GetValue(optNoEncryptFilenames)));

        var optNoDecryptFilenames = new Option<bool>("--keep-names")
        {
            Description = "Keep original filenames, do not decrypt them.",
            Aliases = { "-k" },
        };
        var cmdSaveUnpack = new Command(
            "unpack",
            "Decompress and decrypt a single save file or all save files in a folder (in-place)."
        )
        {
            argSavePath,
            optNoDecryptFilenames
        };
        cmdSaveUnpack.SetAction(parsed =>
            RunSaveUnpack(
                parsed.GetValue(argSavePath)!,
                parsed.GetValue(optNoDecryptFilenames)));

        var cmdSave = new Command("save", "Savegame packing and unpacking.")
        {
            Subcommands = { cmdSaveUnpack, cmdSavePack },
        };

        var cmdRoot = new RootCommand("Modding tooling for Tale of Immortal")
        {
            Subcommands =
            {
                cmdNewMod,
                cmdEdit,
                cmdEncrypt,
                cmdDecrypt,
                cmdMod,
                cmdSave,
            }
        };

        return cmdRoot.Parse(args).Invoke();
    }

    static List<string> ExtendGlobsWithIgnoreFiles(List<string> globs, IReadOnlyList<FileInfo> files)
    {
        static List<string> ReadLines(IReadOnlyList<FileInfo> files)
        {
            var lines = new List<string>();
            foreach (var file in files)
            {
                lines.AddRange(File.ReadLines(file.FullName));
            }
            return lines;
        }

        globs.AddRange(ReadLines(files));
        return globs;
    }

    static int RunEncrypt(FileSystemInfo file)
    {
        if (Directory.Exists(file.FullName))
        {
            foreach (var path in Directory.EnumerateFiles(file.FullName, "*", SearchOption.AllDirectories))
            {
                if (EncryptTool.LooksEncrypted(path))
                    continue;

                Console.Error.WriteLine($"Encrypting '{path}'");
                var data = File.ReadAllBytes(path);
                var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(path, encrypted);
            }
        }
        else if (File.Exists(file.FullName))
        {
            var path = file.FullName;
            if (EncryptTool.LooksEncrypted(path))
                return 0;

            Console.Error.WriteLine($"Encrypting '{path}'");
            var data = File.ReadAllBytes(path);
            var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(path, encrypted);
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: '{file}'");
            return 1;
        }
        return 0;
    }

    static int RunDecrypt(FileSystemInfo file, bool inplace)
    {
        if (Directory.Exists(file.FullName))
        {
            foreach (var path in Directory.EnumerateFiles(file.FullName, "*", SearchOption.AllDirectories))
            {
                if (!EncryptTool.LooksEncrypted(path))
                    continue;

                Console.Error.WriteLine($"Decrypting '{path}'");
                var data = File.ReadAllBytes(path);
                var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(path, decrypted);
            }
        }
        else if (File.Exists(file.FullName))
        {
            var path = file.FullName;
            if (!EncryptTool.LooksEncrypted(path))
                return 0;

            Console.Error.WriteLine($"Decrypting '{path}'");
            var data = File.ReadAllBytes(path);
            var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
            if (inplace)
            {
                File.WriteAllBytes(path, decrypted);
            }
            else
            {
                Console.OpenStandardOutput().Write(decrypted);
            }
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: '{file}'");
            return 1;
        }
        return 0;
    }

    const string SAVE_UNPACK_META_FILENAME = "unpack_metadata.json";

    // For PC, on Android it's gzipped BinaryFormatter serialized objects instead
    static int RunSaveUnpack(FileSystemInfo path, bool keepNames)
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

            if (EncryptTool.LooksEncrypted(filePath))
            {
                meta.IsFileEncrypted = true;
                try
                {
                    var decryptedName = EncryptTool.DecryptDES(outputBasename, EncryptTool.cacheEncryPassword);
                    meta.IsOriginalNameEncrypted = true;
                    if (!keepNames)
                    {
                        meta.IsNameDecrypted = true;
                        outputBasename = decryptedName;
                    }
                }
                catch (FormatException) // Invalid base64, etc
                { }

                var bytes = File.ReadAllBytes(filePath);
                var decrypted = EncryptTool.DecryptMult(bytes, EncryptTool.cacheEncryPassword);
                var decompressed = Decompress(decrypted);

                try
                {
                    using var doc = JsonDocument.Parse(decompressed);
                    decompressed = Encoding.UTF8.GetBytes(PrettyJsonSerialize(doc.RootElement));
                }
                catch { }

                // In-place
                var outFile = Path.Combine(rootPath, outputBasename + ext);
                Console.Error.WriteLine($"`{filePath}` >> `{outFile}`");

                File.Delete(filePath);
                File.WriteAllBytes(outFile, decompressed);

                metadata.Files[Path.GetRelativePath(path.FullName, outFile)] = meta;
            }
            else
            {
                Console.Error.WriteLine($"`{filePath}` isn't encrypted, skipped.");
                metadata.Files[Path.GetRelativePath(path.FullName, filePath)] = meta;
            }
        }

        if (Directory.Exists(path.FullName))
        {
            foreach (var file in Directory.EnumerateFiles(path.FullName, "*", SearchOption.AllDirectories))
            {
                ProcessFile(file);
            }

            var metaPath = Path.Combine(path.FullName, SAVE_UNPACK_META_FILENAME);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(
                metadata,
                new SourceGenerationContext(jsonPrettySerializerOptions).SaveUnpackMetadata));
        }
        else if (File.Exists(path.FullName))
        {
            ProcessFile(path.FullName);
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: `{path}`");
            return 1;
        }

        return 0;
    }

    static int RunSavePack(FileSystemInfo path, bool keepNames)
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
                return;

            var rootPath = Path.GetDirectoryName(filePath)!;
            var outputBasename = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath);

            var bytes = File.ReadAllBytes(filePath);
            try
            {
                // Minify json before packing
                using var doc = JsonDocument.Parse(bytes);
                bytes = Encoding.UTF8.GetBytes(JsonSerialize(doc.RootElement));
            }
            catch { }

            var compressed = Compress(bytes);
            var encrypted = EncryptTool.EncryptMult(compressed, EncryptTool.cacheEncryPassword);

            if (!keepNames && (meta == null || (meta.IsOriginalNameEncrypted && meta.IsNameDecrypted)))
            {
                outputBasename = EncryptTool.EncryptDES(outputBasename, EncryptTool.cacheEncryPassword);
            }

            var outFile = Path.Combine(rootPath, outputBasename + ext);
            Console.Error.WriteLine($"`{filePath}` >> `{outFile}`");

            File.Delete(filePath);
            File.WriteAllBytes(outFile, encrypted);
        }

        if (Directory.Exists(path.FullName))
        {
            var metaPath = Path.Combine(path.FullName, SAVE_UNPACK_META_FILENAME);
            if (!File.Exists(metaPath))
            {
                Console.Error.WriteLine("Metadata file not found. Run save-unpack first.");
                return 1;
            }
            var metadata = JsonSerializer.Deserialize(
                File.ReadAllBytes(metaPath),
                SourceGenerationContext.Default.SaveUnpackMetadata)!;

            foreach (var kv in metadata.Files)
            {
                var relPath = kv.Key;
                var meta = kv.Value;
                var filePath = Path.Combine(path.FullName, relPath);

                ProcessFile(filePath, meta);
            }

            Console.Error.WriteLine("Pack complete, removing metadata file...");
            File.Delete(metaPath);
        }
        else if (File.Exists(path.FullName))
        {
            ProcessFile(path.FullName);
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: `{path}`");
            return 1;
        }

        return 0;
    }

    static int RunModRestoreExcel(DirectoryInfo folder)
    {
        var root = folder.FullName;
        var exportPath = Path.Combine(root, ModInfo.exportDataFileName);

        if (File.Exists(exportPath))
        {
            var modInfo = GetModInfo(root, ModInfo.InfoCollectOption.ExportData);
            if (modInfo is null)
                return 1;

            if (modInfo.ProjectData.ExcelEncrypt)
            {
                Console.Error.WriteLine($"Disabling excelEncrypt in '{exportPath}'");
                modInfo.ProjectData.ExcelEncrypt = false;

                var exportJson = Encoding.UTF8.GetBytes(PrettyJsonSerialize(modInfo.AsExportData()));
                File.WriteAllBytes(exportPath, EncryptTool.EncryptMult(exportJson, EncryptTool.modEncryPassword));
            }
            else
            {
                Console.Error.WriteLine($"excelEncrypt is already disabled in '{exportPath}'");
            }
        }

        foreach (var file in GetFilesByPattern(root, @"\.png$"))
        {
            var relPath = Path.GetRelativePath(root, file);
            var firstPart = PathUtils.Split(relPath).ElementAtOrDefault(0);

            if (firstPart == "ModAssets"
                || firstPart == "ModCode"
                || firstPart == "ModExcel")
                continue;

            if (EncryptTool.LooksEncrypted(file))
                continue;

            Console.Error.WriteLine($"Encrypting '{file}'");
            var data = File.ReadAllBytes(file);
            File.WriteAllBytes(file, EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword));
        }

        // Decrypt any encrypted json file, even if outside ModExcel
        foreach (var file in GetFilesByPattern(root, @"\.json$"))
        {
            if (!EncryptTool.LooksEncrypted(file))
                continue;

            Console.Error.WriteLine($"Decrypting '{file}'");
            var data = File.ReadAllBytes(file);
            File.WriteAllBytes(file, EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword));
        }
        return 0;
    }

    static int RunModPack(
        DirectoryInfo folder,
        DirectoryInfo? outputFolder,
        List<string> ignoreGlobs,
        bool noIgnoreFiles = false,
        bool cleanOutput = false,
        string outputFormat = packOutputNameTemplate,
        FileInfo? readmeFile = null)
    {
        var root = folder.FullName;

        var modInfo = GetModInfo(root);
        if (modInfo is null)
            return 1;

        var readmePathDefault = Path.Combine(root, "README.md");
        var readmePath = readmeFile?.FullName ?? readmePathDefault;
        if (readmeFile != null
            || (string.IsNullOrWhiteSpace(modInfo.ProjectData.Desc)
                && File.Exists(readmePathDefault)))
        {
            var readmeText = ToiMarkup.FromMarkdown(File.ReadAllText(readmePath).ReplaceLineEndings("\n"));
            modInfo.ProjectData.Desc = "\n" + readmeText;
        }

        var outRoot = ResolvePathPlaceholder(outputFolder?.FullName, outputFormat, new()
        {
            { "SoleId", modInfo.ProjectData.SoleID },
            { "FolderName", PathUtils.GetBaseName(outputFolder?.FullName ?? "") }
        }) ?? root;

        Console.Error.WriteLine($"Setting up output folder...\nOutput Folder: '{outRoot}'");

        CookMod(root, outRoot, modInfo, ignoreGlobs, noIgnoreFiles, clean: cleanOutput);

        Console.Error.WriteLine("Successfully packed to:");
        Console.Out.WriteLine(outRoot);

        return 0;
    }

    static int RunModUnpack(DirectoryInfo folder)
    {
        // TODO: Add an option for separate output folder
        var root = folder.FullName;
        var exportPath = Path.Combine(root, ModInfo.exportDataFileName);

        var modInfo = GetModInfo(root, ModInfo.InfoCollectOption.ExportData);
        if (modInfo is null)
            return 1;

        // Decrypt all the files if encrypted
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!EncryptTool.LooksEncrypted(file))
                continue;

            // Skip ModExportData.cache metadata file
            if (Path.GetFileName(file)
                .Equals(ModInfo.exportDataFileName, PathUtils.StringComparison))
                continue;

            Console.Error.WriteLine($"Decrypting '{file}'");
            var data = File.ReadAllBytes(file);
            var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, decrypted);
        }

        if (modInfo.ExportItems is JsonObject items)
        {
            var jsonDir = Path.Combine(root, "ModExcel", "ModExportDataJson");
            Directory.CreateDirectory(jsonDir);

            foreach (var (filename, data) in items)
            {
                var filePath = Path.Combine(jsonDir, filename + ".json");
                Console.Error.WriteLine($"Unpacking ModExportData's {filename} to `{filePath}`");
                File.WriteAllText(filePath,
                    PrettyJsonSerialize(data),
                    Encoding.UTF8
                );
            }
        }

        // Write ModProject.cache
        var projPath = Path.Combine(root, ModInfo.projectDataFileName);
        File.WriteAllText(projPath,
            modInfo.ProjectData.ToJsonString(jsonPrettySerializerOptions),
            Encoding.UTF8);

        // Write ModData.cache
        var modData = NewModDataCache(modInfo.ProjectData.SoleID);
        var modDataPath = Path.Combine(root, ModInfo.modDataFileName);
        File.WriteAllText(modDataPath,
            PrettyJsonSerialize(modData),
            Encoding.UTF8);

        // Delete ModExportData.cache
        File.Delete(exportPath);

        Console.Error.WriteLine("Unpack completed.");
        return 0;
    }

    static int RunNewModProject(string name)
    {
        // Generate IDs
        string soleId = ProjectData.GenerateSoleId();

        string root = $"Mod_{soleId}_{name}";
        if (Directory.Exists(root))
        {
            Console.Error.WriteLine($"Error: Folder '{root}' already exists.");
            return 1;
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "ModAssets"));
        Directory.CreateDirectory(Path.Combine(root, "ModCode", "ModMain"));
        Directory.CreateDirectory(Path.Combine(root, "ModExcel"));

        File.WriteAllText(Path.Combine(root, "README.md"), $"# {name}");

        // ModData.cache
        var modData = NewModDataCache(soleId);

        File.WriteAllText(Path.Combine(root, ModInfo.modDataFileName), PrettyJsonSerialize(modData));

        // ModProject.cache
        var projectData = new ProjectData($"Mod {soleId} {name}", soleId).ToJsonNode();

        File.WriteAllText(Path.Combine(root, ModInfo.projectDataFileName), PrettyJsonSerialize(projectData));

        Console.Error.WriteLine($"New mod template created at '{root}'");
        return 0;
    }

    static int RunEdit(FileInfo inputFile)
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

        if (!inputFile.Exists)
        {
            Console.Error.WriteLine("File not found: " + inputFile);
            return 1;
        }

        if (!EncryptTool.LooksEncrypted(inputFile.FullName))
        {
            Console.Error.WriteLine("File is already decrypted: " + inputFile);
            return 1;
        }

        var data = File.ReadAllBytes(inputFile.FullName);
        var decryptedData = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
        var decryptedText = Encoding.UTF8.GetString(decryptedData);

        string formattedJson;
        try
        {
            var doc = JsonDocument.Parse(decryptedText);
            formattedJson = PrettyJsonSerialize(doc.RootElement);
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

            using (var proc = Process.Start(psi)!)
            {
                proc.WaitForExit();
            }

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
            File.WriteAllBytes(inputFile.FullName, EncryptTool.EncryptMult(modifiedData, EncryptTool.modEncryPassword));

            Console.Error.WriteLine($"Updated file '{inputFile}'");
            return 0;
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    static JsonObject NewModDataCache(string? soleId)
    {
        static JsonObject EmptyGroup()
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

    static void CookMod(
        string modDir,
        string cookDir,
        ModInfo modInfo,
        List<string>? ignoreGlobs = null,
        bool noIgnoreFiles = false,
        bool clean = false)
    {
        if (clean
            && Directory.Exists(cookDir)
            && !PathUtils.Equals(modDir, cookDir))
        {
            Console.Error.WriteLine($"Deleting already existing cooked directory: \"{cookDir}\"");
            Directory.Delete(cookDir, recursive: true);
        }

        CopyModWithIgnores(modDir, cookDir, ignoreGlobs ?? [], noIgnoreFiles, ignoreModCode: true);
        TryCookModCode(modDir, cookDir, modInfo.ModNamespace, clean);

        var projectDataPath = Path.Combine(cookDir, ModInfo.projectDataFileName);
        var modDataPath = Path.Combine(cookDir, ModInfo.modDataFileName);
        var exportDataPath = Path.Combine(cookDir, ModInfo.exportDataFileName);

        Console.Error.WriteLine($"Writing {ModInfo.exportDataFileName}");
        var exportJsonBytes = Encoding.UTF8.GetBytes(
            PrettyJsonSerialize(modInfo.AsExportData())
        );
        var exportEncrypted = EncryptTool.EncryptMult(exportJsonBytes, EncryptTool.modEncryPassword);
        File.WriteAllBytes(exportDataPath, exportEncrypted);

        if (File.Exists(projectDataPath))
        {
            Console.Error.WriteLine($"Deleting {ModInfo.projectDataFileName}");
            File.Delete(projectDataPath);
        }
        if (File.Exists(modDataPath))
        {
            Console.Error.WriteLine($"Deleting {ModInfo.modDataFileName}");
            File.Delete(modDataPath);
        }

        // Encrypting/decrypting ModExcel/**/*.json
        var modExcelDir = Path.Combine(cookDir, "ModExcel");
        if (Directory.Exists(modExcelDir))
        {
            foreach (var file in GetFilesByPattern(modExcelDir, @"\.json$"))
            {
                if (modInfo.ProjectData.ExcelEncrypt == EncryptTool.LooksEncrypted(file))
                    continue;

                byte[] writeBytes;
                var bytes = File.ReadAllBytes(file);
                if (modInfo.ProjectData.ExcelEncrypt)
                {
                    Console.Error.WriteLine($"Encrypting '{file}'");
                    writeBytes = EncryptTool.EncryptMult(bytes, EncryptTool.modEncryPassword);
                }
                else
                {
                    Console.Error.WriteLine($"Decrypting '{file}'");
                    writeBytes = EncryptTool.DecryptMult(bytes, EncryptTool.modEncryPassword);
                }
                File.WriteAllBytes(file, writeBytes);
            }
        }

        // Encrypting **/*.png except ModAssets/, ModCode/, and ModExcel/
        // TODO: Look at what the in-game mod creator does and follow it instead
        foreach (var file in GetFilesByPattern(cookDir, @"\.png$"))
        {
            var relPath = Path.GetRelativePath(cookDir, file);
            var firstPart = PathUtils.Split(relPath).ElementAtOrDefault(0);

            if (firstPart == "ModAssets"
                || firstPart == "ModCode"
                || firstPart == "ModExcel")
                continue;

            if (EncryptTool.LooksEncrypted(file))
                continue;

            Console.Error.WriteLine($"Encrypting '{file}'");
            var data = File.ReadAllBytes(file);
            var enc = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, enc);
        }
    }

    /// <summary>
    /// Try copying compiled ModCode Release artifacts <br/>
    /// Assumes -p:AppendTargetFrameworkToOutputPath=false
    /// </summary>
    static bool TryCookModCode(
        string modDir,
        string cookDir,
        string? modNamespace,
        bool clean = false)
    {
        if (string.IsNullOrWhiteSpace(modNamespace))
        {
            Console.Error.WriteLine("modNamespace not found, skipping ModCode");
            return false;
        }

        var releaseDir = Path.Combine(modDir, "ModCode", "ModMain", "bin", "Release");
        var modMainDll = Path.Combine(releaseDir, $"{modNamespace}.dll");

        Console.Error.WriteLine($"modNamespace: {modNamespace}");
        if (!File.Exists(modMainDll))
        {
            Console.Error.WriteLine($"Couldn't find '{modNamespace}.dll', skipping ModCode");
            return false;
        }

        // Copy over all files from `ModCode/ModMain/bin/Release` if main dll exists
        // This is the behaviour of the Game's in-game mod tooling as well.

        var dllOutDir = Path.Combine(cookDir, "ModCode", "dll");
        if (clean && Directory.Exists(dllOutDir))
        {
            Console.Error.WriteLine($"Deleting already existing directory: \"{dllOutDir}\"");
            Directory.Delete(dllOutDir, recursive: true);
        }
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

        return true;
    }

    static void CopyModWithIgnores(
        string from,
        string to,
        List<string> ignoreGlobs,
        bool noIgnoreFiles,
        bool ignoreModCode = false)
    {
        // Skip copying if both are the same directory
        if (PathUtils.Equals(from, to))
            return;

        Directory.CreateDirectory(to);

        foreach (var srcPath in new IgnoreWalk(
            [from],
            Overrides: ignoreGlobs,
            UseIgnoreFiles: !noIgnoreFiles).Enumerate())
        {
            var relPath = Path.GetRelativePath(from, srcPath);
            var relParts = PathUtils.Split(relPath);
            var targetPath = Path.Combine(to, relPath);

            // XXX Could've used ignoreGlobs if the Ignore API supported rooted patterns
            if (ignoreModCode && relParts.ElementAtOrDefault(0) == "ModCode")
                continue;

            if (Directory.Exists(srcPath))
            {
                Directory.CreateDirectory(targetPath);
            }
            else
            {
                Console.Error.WriteLine($"Copying: '{srcPath}' -> '{targetPath}'");
                File.Copy(srcPath, targetPath, true);
            }
        }
    }

    static string? ResolvePathPlaceholder(
        string? path,
        string format,
        Dictionary<string, string> placeholderValues)
    {
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(format))
        {
            var name = PathUtils.GetBaseName(path);
            var formattedName = RegexTextPlaceholder.Replace(format, match =>
            {
                var key = match.Groups["placeholder"].Value;
                return placeholderValues.TryGetValue(key, out var value) ? value : match.Value;
            });
            if (!string.IsNullOrWhiteSpace(formattedName))
                name = formattedName;

            path = PathUtils.WithBaseName(path, name);
        }

        return path;
    }

    static ModInfo? GetModInfo(
        string modFolder,
        ModInfo.InfoCollectOption infoCollect = ModInfo.InfoCollectOption.Any)
    {
        try
        {
            return new(modFolder, infoCollect);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return null;
        }
    }

    public static IEnumerable<string> GetFilesByPattern(
        string path,
        string rePattern = "",
        SearchOption searchOption = SearchOption.AllDirectories)
    {
        var re = new Regex(rePattern, RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(path, "*", searchOption)
            .Where(file => re.IsMatch(Path.GetFileName(file)));
    }

    static string PrettyJsonSerialize(JsonNode? node)
    {
        return node?.ToJsonString(jsonPrettySerializerOptions) ?? "null";
    }

    static string PrettyJsonSerialize(JsonElement element) => JsonSerialize(element, jsonPrettyWriterOptions);

    static string JsonSerialize(JsonElement element, JsonWriterOptions jsonWriterOptions = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, jsonWriterOptions))
        {
            element.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [GeneratedRegex(@"\{\{\s*(?<placeholder>\w+)\s*\}\}", RegexOptions.Compiled)]
    static partial Regex RegexTextPlaceholder { get; }
}

class SaveUnpackMetadata
{
    public Dictionary<string, FileMeta> Files { get; set; } = [];

    public class FileMeta
    {
        public bool IsFileEncrypted { get; set; }
        public bool IsOriginalNameEncrypted { get; set; }
        public bool IsNameDecrypted { get; set; }
    }
}

[JsonSerializable(typeof(SaveUnpackMetadata))]
[JsonSerializable(typeof(ProjectData))]
internal partial class SourceGenerationContext : JsonSerializerContext { }
