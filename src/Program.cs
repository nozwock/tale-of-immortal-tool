using System.Buffers;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.GZip;

namespace TaleOfImmortalTool;

partial class Program
{
    static readonly string packOutputNameTemplate = "Mod_{{SoleId}}_{{FolderName}}";

    static readonly JsonSerializerOptions jsonPrettySerializerOptions = new()
    {
        WriteIndented = true,
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
            + " Useful for editing 'ModExportData.cache'."
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

        var cmdRestoreExcel = new Command(
            "restore",
            "Decrypt .json mod files (in-place), and set `excelEncrypt=false` in `ModExportData.cache`."
        )
        {
            argModFolder
        };
        cmdRestoreExcel.SetAction(parsed =>
            RunRestoreExcel(parsed.GetValue(argModFolder)!));

        var optPackOutput = new Option<DirectoryInfo?>("--output")
        {
            Description = "Output folder. Defaults to input folder.",
            Aliases = { "-o" },
        }.AcceptLegalFilePathsOnly();
        var optPackOutputFormat = new Option<string?>("--output-format")
        {
            Description = "Output folder name format. Set it to empty string to disable formatting.",
            DefaultValueFactory = _ => packOutputNameTemplate,
        };
        var cmdPack = new Command(
            "pack",
            """
            Pack mod files, so that the mod can be loaded in-game.
            Don't try to pack mod made using the in-game mod creator, as ModData.cache is largely ignored except for `.modNamespace`.
            Dlls and related assets from 'ModCode/ModMain/bin/Release/' gets copied over to 'ModCode/dll/'.
            """
        )
        {
            argModFolder,
            optPackOutput,
            optCleanOutput,
            optIgnoreGlobs,
            optIgnoreFiles,
            optNoIgnoreFile,
            optPackOutputFormat,
        };
        // TODO: README file's contents in modexportdata's description
        cmdPack.SetAction(parsed =>
            RunModPack(
                folder: parsed.GetValue(argModFolder)!,
                outputFolder: parsed.GetValue(optPackOutput),
                outputFormat: parsed.GetValue(optPackOutputFormat)!,
                ignoreGlobs: ExtendGlobsWithIgnoreFiles(
                    parsed.GetValue(optIgnoreGlobs)!,
                    parsed.GetValue(optIgnoreFiles)!),
                noIgnoreFiles: parsed.GetValue(optNoIgnoreFile),
                cleanOutput: parsed.GetValue(optCleanOutput)));

        var cmdUnpack = new Command(
            "unpack",
            "Unpack mod files (in-place), so that the mod can be edited in the in-game mod editor."
        )
        {
            argModFolder
        };
        cmdUnpack.SetAction(parsed =>
            RunModUnpack(parsed.GetValue(argModFolder)!));

        var cmdMod = new Command("mod", "Mod files packing and unpacking.")
        {
            Subcommands = { cmdRestoreExcel, cmdPack, cmdUnpack },
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

    static int RunEncrypt(FileSystemInfo path)
    {
        if (Directory.Exists(path.FullName))
        {
            foreach (var file in Directory.EnumerateFiles(path.FullName, "*", SearchOption.AllDirectories))
            {
                var data = File.ReadAllBytes(file);
                if (EncryptTool.LooksEncrypted(data))
                {
                    continue;
                }

                Console.Error.WriteLine($"Encrypting '{file}'");
                var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                File.WriteAllBytes(file, encrypted);
            }
        }
        else if (File.Exists(path.FullName))
        {
            var file = path;
            var data = File.ReadAllBytes(file.FullName);
            if (EncryptTool.LooksEncrypted(data))
            {
                return 0;
            }

            Console.Error.WriteLine($"Encrypting '{file}'");
            var encrypted = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file.FullName, encrypted);
        }
        else
        {
            Console.Error.WriteLine($"Neither a file nor directory: '{path}'");
            return 1;
        }
        return 0;
    }

    static int RunDecrypt(FileSystemInfo path, bool inplace)
    {
        if (Directory.Exists(path.FullName))
        {
            foreach (var file in Directory.EnumerateFiles(path.FullName, "*", SearchOption.AllDirectories))
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
        else if (File.Exists(path.FullName))
        {
            var file = path.FullName;
            var data = File.ReadAllBytes(file);
            if (!EncryptTool.LooksEncrypted(data))
            {
                return 0;
            }

            Console.Error.WriteLine($"Decrypting '{file}'");
            var decrypted = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
            if (inplace)
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
            Console.Error.WriteLine($"Neither a file nor directory: '{path}'");
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

            var bytes = File.ReadAllBytes(filePath);
            if (EncryptTool.LooksEncrypted(bytes))
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
                PrettyJsonTypeInfo(SourceGenerationContext.Default.SaveUnpackMetadata)));
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
                bytes = Encoding.UTF8.GetBytes(PrettyJsonSerialize(doc.RootElement));
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

    static int RunRestoreExcel(DirectoryInfo folder)
    {
        var root = folder.FullName;

        byte[] data;

        var exportPath = Path.Combine(root, "ModExportData.cache");
        if (File.Exists(exportPath))
        {
            var decrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);

            // Set projectData.excelEncrypt = false
            var json = JsonNode.Parse(decrypted);
            if (json?["projectData"]?["excelEncrypt"] != null && json["projectData"]!["excelEncrypt"]!.GetValue<bool>())
            {
                Console.Error.WriteLine($"Disabling excelEncrypt in '{exportPath}'");
                json["projectData"]!["excelEncrypt"] = false;

                var modified = Encoding.UTF8.GetBytes(PrettyJsonSerialize(json));
                File.WriteAllBytes(exportPath, EncryptTool.EncryptMult(modified, EncryptTool.modEncryPassword));
            }
            else
            {
                Console.Error.WriteLine($"excelEncrypt is already disabled in '{exportPath}'");
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

            Console.Error.WriteLine($"Encrypting '{file}'");
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

            Console.Error.WriteLine($"Decrypting '{file}'");
            File.WriteAllBytes(file, EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword));
        }
        return 0;
    }

    static int RunModPack(
        DirectoryInfo folder,
        DirectoryInfo? outputFolder,
        string outputFormat,
        List<string> ignoreGlobs,
        bool noIgnoreFiles = false,
        bool cleanOutput = false)
    {
        static void SetupOutputModFolder(
            string input,
            string output,
            string? modNamespace,
            List<string> ignoreGlobs,
            bool noIgnoreFiles)
        {
            // Copy compiled ModCode Release artifacts
            if (modNamespace != null)
            {
                var releaseDir = Path.Combine(input, "ModCode", "ModMain", "bin", "Release");
                var modMainDll = Path.Combine(releaseDir, $"{modNamespace}.dll");

                Console.Error.WriteLine($"modNamespace: {modNamespace}");
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

            foreach (var srcPath in new IgnoreWalk(
                [input],
                Overrides: ignoreGlobs,
                UseIgnoreFiles: !noIgnoreFiles).Enumerate())
            {
                var relPath = Path.GetRelativePath(input, srcPath);
                var targetPath = Path.Combine(output, relPath);

                // Skip everything under ModCode except dll folder
                if (relPath.StartsWith("ModCode"))
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
                    Console.Error.WriteLine($"Copying: '{srcPath}' -> '{targetPath}'");
                    File.Copy(srcPath, targetPath, true);
                }
            }
        }

        var root = folder.FullName;
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
            Console.Error.WriteLine($"Missing required ModProject.cache in root folder: \"{projPath}\"");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(soleId))
        {
            Console.Error.WriteLine("Cannot find soleID in mod's metadata.");
            return 1;
        }

        var modDataPath = Path.Combine(root, "ModData.cache");
        if (File.Exists(modDataPath))
        {
            var modDataRoot = JsonNode.Parse(File.ReadAllText(modDataPath, Encoding.UTF8))!.AsObject();
            // Important: For dll mods, mod won't be loaded at all if correct namespace is not filled in
            exportRoot["modNamespace"] = exportRoot["modNamespace"]?.GetValue<string?>() ?? modDataRoot["modNamespace"]?.GetValue<string?>();
        }
        exportRoot["modNamespace"] = exportRoot["modNamespace"]?.GetValue<string?>() ?? $"MOD_{soleId}";

        var outRoot = outputFolder?.FullName ?? root;
        if (!string.IsNullOrWhiteSpace(outputFormat))
        {
            var outName = PathUtils.GetBaseName(outRoot);

            var placeholderValues = new Dictionary<string, string>
            {
                ["SoleId"] = soleId,
                ["FolderName"] = outName,
            };
            var outFormattedName = RegexTextPlaceholder.Replace(outputFormat, match =>
            {
                var key = match.Groups["placeholder"].Value;
                return placeholderValues.TryGetValue(key, out var value) ? value : match.Value;
            });
            if (!string.IsNullOrWhiteSpace(outFormattedName))
                outName = outFormattedName;

            outRoot = PathUtils.WithBaseName(outRoot, outName);
        }

        Console.Error.WriteLine($"Setting up output folder...\nOutput Folder: '{outRoot}'");
        if (cleanOutput && Directory.Exists(outRoot))
        {
            Console.Error.WriteLine($"Clean output mode: Deleting output folder first...");
            Directory.Delete(outRoot, recursive: true);
        }
        var modNamespace = exportRoot["modNamespace"]?.GetValue<string?>();
        SetupOutputModFolder(root, outRoot, modNamespace, ignoreGlobs, noIgnoreFiles);
        root = outRoot; // Operating in output folder now
        projPath = Path.Combine(root, "ModProject.cache");
        modDataPath = Path.Combine(root, "ModData.cache");
        exportPath = Path.Combine(root, "ModExportData.cache");

        Console.Error.WriteLine("Writing 'ModExportData.cache'");

        var exportJsonBytes = Encoding.UTF8.GetBytes(
            PrettyJsonSerialize(exportRoot)
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
                    Console.Error.WriteLine($"Encrypting '{file}'");
                    bytes = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
                }
                else
                {
                    Console.Error.WriteLine($"Decrypting '{file}'");
                    bytes = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
                }
                File.WriteAllBytes(file, bytes);
            }
        }

        foreach (var file in GetFilesByPattern(root, @"(\.png)$"))
        {
            // Skip ModAssets/ and ModCode/
            var relPath = Path.GetRelativePath(root, file);
            if (relPath.StartsWith("ModAssets") || relPath.StartsWith("ModCode") || relPath.StartsWith("ModExcel"))
                continue;

            var data = File.ReadAllBytes(file);
            if (EncryptTool.LooksEncrypted(data))
            {
                continue;
            }

            Console.Error.WriteLine($"Encrypting '{file}'");
            var enc = EncryptTool.EncryptMult(data, EncryptTool.modEncryPassword);
            File.WriteAllBytes(file, enc);
        }

        Console.Error.WriteLine("Successfully packed to:");
        Console.Out.WriteLine(outRoot);

        return 0;
    }

    static int RunModUnpack(DirectoryInfo folder)
    {
        // TODO: Add an option for separate output folder
        var root = folder.FullName;
        var exportPath = Path.Combine(root, "ModExportData.cache");

        if (!File.Exists(exportPath))
        {
            Console.Error.WriteLine($"Missing required ModExportData.cache in root folder: \"{exportPath}\"");
            return 1;
        }

        var exportDecrypted = EncryptTool.DecryptMult(File.ReadAllBytes(exportPath), EncryptTool.modEncryPassword);
        var exportNode = JsonNode.Parse(Encoding.UTF8.GetString(exportDecrypted))!.AsObject();

        if (exportNode["projectData"] is not JsonObject projectData)
        {
            Console.Error.WriteLine("ModExportData.cache missing projectData.");
            return 1;
        }

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

            Console.Error.WriteLine($"Decrypting '{file}'");
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
                    PrettyJsonSerialize(data),
                    Encoding.UTF8
                );
            }
        }

        // Write ModProject.cache
        var projPath = Path.Combine(root, "ModProject.cache");
        File.WriteAllText(projPath,
            PrettyJsonSerialize(projectData),
            Encoding.UTF8);

        var soleId = projectData["soleID"]?.GetValue<string>();
        var modData = NewModDataCache(soleId);
        var modDataPath = Path.Combine(root, "ModData.cache");
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
        static string GenerateSoleId()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string([.. Enumerable.Range(0, 6).Select(_ => chars[Random.Shared.Next(chars.Length)])]);
        }

        // Generate IDs
        string soleId = GenerateSoleId();
        long createTicks = DateTime.UtcNow.Ticks;

        string root = $"Mod_{soleId} {name}";
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

        File.WriteAllText(Path.Combine(root, "ModData.cache"), PrettyJsonSerialize(modData));

        // ModProject.cache
        var modProject = new JsonObject
        {
            ["soleID"] = soleId,
            ["createTicks"] = createTicks,
            ["name"] = $"Mod {soleId} {name}",
            ["author"] = "unknown",
            ["desc"] = $"Creation Time {DateTime.Now:yyyy_MM_dd_HH_mm_ss}",
            ["ver"] = "1.0.0",
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

        File.WriteAllText(Path.Combine(root, "ModProject.cache"), PrettyJsonSerialize(modProject));

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

        byte[] data = File.ReadAllBytes(inputFile.FullName);
        if (!EncryptTool.LooksEncrypted(data))
        {
            Console.Error.WriteLine("File is already decrypted: " + inputFile);
            return 1;
        }

        byte[] decryptedData = EncryptTool.DecryptMult(data, EncryptTool.modEncryPassword);
        string decryptedText = Encoding.UTF8.GetString(decryptedData);

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

    public static IEnumerable<string> GetFilesByPattern(string path,
                       string rePattern = "",
                       SearchOption searchOption = SearchOption.AllDirectories)
    {
        var re = new Regex(rePattern, RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(path, "*", searchOption)
                        .Where(file =>
                                 re.IsMatch(Path.GetFileName(file)));
    }

    static string PrettyJsonSerialize(JsonNode? node)
    {
        return node?.ToJsonString(jsonPrettySerializerOptions) ?? "null";
    }

    static string PrettyJsonSerialize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        element.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static JsonTypeInfo<T> PrettyJsonTypeInfo<T>(JsonTypeInfo<T> typeInfo)
    {
        typeInfo.Options.WriteIndented = true;
        typeInfo.Options.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        return typeInfo;
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
internal partial class SourceGenerationContext : JsonSerializerContext { }
