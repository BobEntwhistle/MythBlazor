
using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WsdlToOpenApi;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        const string apiProject = "MythTvApi";
        const string thisProject = "WsdlToOpenApi.Runner";
        // determine project root (walk up from AppContext.BaseDirectory looking for the runner .csproj)
        var apiFolder = FindProjectRoot($"{apiProject}") ?? Directory.GetCurrentDirectory();
        var projectFolder = FindProjectRoot(thisProject) ?? Directory.GetCurrentDirectory();

        var root = new RootCommand("WsdlToOpenApi runner: generate OpenAPI files from WSDL URLs and run Kiota when needed.");
        Option<FileInfo?> fileOption = new("--config", "-c") { DefaultValueFactory = fileName => new FileInfo(Path.Combine(projectFolder, "wsdls.json")), Description = "JSON file containing array of WSDL URLs" };
        Option<DirectoryInfo?> outDirOption = new("--out", "-o") { DefaultValueFactory = outFolder => new DirectoryInfo(Path.Combine(apiFolder, "Temp")), Description = "Output directory for OpenAPI files (relative to project)" };
        root.Add(fileOption);
        root.Add(outDirOption);

        root.SetAction(async (ParseResult parseResult) =>
        {
            var cfg = parseResult.GetValue(fileOption) ?? new FileInfo("wsdls.json");
            var outDir = parseResult.GetValue(outDirOption) ?? new DirectoryInfo("Temp");


            if (!cfg.Exists)
            {
                Console.WriteLine($"Config file not found: {cfg.FullName}");
                return;
            }

            if (!outDir.Exists) outDir.Create();

            var urls = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(cfg.FullName)) ?? Array.Empty<string>();
            var converter = new WsdlToOpenApiConverter();


            var stateFile = Path.Combine(outDir.FullName, "kiota-state.json");
            var state = File.Exists(stateFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(stateFile)) ?? new() : new();

            foreach (var url in urls)
            {
                try
                {
                    Console.WriteLine($"Processing {url}...");
                    var json = await converter.GenerateOpenApiFromWsdlAsync(url);
                    var uri = new Uri(url);

                    // use the first non-empty path segment (e.g. /Video/wsdl -> "Video")
                    var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var firstSegment = segs.Length > 0 ? segs[0].Trim('/').Split('.').FirstOrDefault() ?? "schema" : "schema";
                    var safeName = SanitizeIdentifier(firstSegment);

                    var outFile = Path.Combine(outDir.FullName, safeName + ".openapi.json");
                    await File.WriteAllTextAsync(outFile, json);

                    // compute md5
                    using var md5 = MD5.Create();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    var hash = BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

                    var existingHash = state.ContainsKey(outFile) ? state[outFile] : null;

                    // client output folder under project root, named {clientName}.client
                    var clientDir = Path.Combine(apiFolder, safeName + ".client");

                    var needKiota = existingHash != hash || !Directory.Exists(clientDir);
                    if (needKiota)
                    {
                        Console.WriteLine($"Running kiota for {outFile} -> {clientDir}");
                        // ensure target folder exists (kiota will create, but ensure parent exists)
                        Directory.CreateDirectory(clientDir);

                        // Build kiota CLI args:
                        // - generate C# client
                        // - put output in project root {client}.client
                        // - set namespace to MythTvApi
                        // - use HttpClient and System.Text.Json (suitable for Blazor)
                        var namespaceArg = $"{apiProject}.{safeName}";
                        var argsStr = $"generate -l csharp -d \"{outFile}\" -o \"{clientDir}\" --namespace-name \"{namespaceArg}\"";

                        var psi = new System.Diagnostics.ProcessStartInfo("kiota", argsStr)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        var p = System.Diagnostics.Process.Start(psi)!;
                        var stdout = await p.StandardOutput.ReadToEndAsync();
                        var stderr = await p.StandardError.ReadToEndAsync();
                        p.WaitForExit();
                        Console.WriteLine(stdout);
                        if (p.ExitCode != 0)
                        {
                            Console.WriteLine("Kiota failed:");
                            Console.WriteLine(stderr);
                        }
                        else
                        {
                            // update state on success
                            state[outFile] = hash;
                            await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Skipping kiota for {outFile} (no changes and client exists)");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed {url}: {ex.Message}");
                }
            }
        });

        ParseResult parseResult = root.Parse(args);
        return await parseResult.InvokeAsync();
    }

    // Try to find the project root by looking for the given project file name in parent folders
    private static string? FindProjectRoot(string apiFolderName)
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, apiFolderName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }

            // fallback: look for a .sln file
            dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.EnumerateFiles(dir.FullName, "*.slnx").Any())
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Schema";
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(ch);
            }
            else if (ch == '-' || ch == ' ')
            {
                sb.Append('_');
            }
        }
        var outName = sb.ToString();
        // ensure it starts with a letter or underscore
        if (string.IsNullOrEmpty(outName)) return "Schema";
        if (!char.IsLetter(outName[0]) && outName[0] != '_')
        {
            outName = "_" + outName;
        }
        return outName;
    }
}