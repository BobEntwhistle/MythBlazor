using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using WsdlToOpenApi;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("WsdlToOpenApi runner: generate OpenAPI files from WSDL URLs and run Kiota when needed.");
        Option<FileInfo?> fileOption = new("--config", "-c") { DefaultValueFactory = fileName => new FileInfo("wsdls.json"), Description = "JSON file containing array of WSDL URLs" };
        Option<DirectoryInfo?> outDirOption = new("--out", "-o") { DefaultValueFactory = outFolder => new DirectoryInfo("Temp"), Description = "Output directory for OpenAPI files (relative to project)" };
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

            var runnerDir = AppContext.BaseDirectory;
            var stateFile = Path.Combine(outDir.FullName, "kiota-state.json");
            var state = File.Exists(stateFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(stateFile)) ?? new() : new();

            foreach (var url in urls)
            {
                try
                {
                    Console.WriteLine($"Processing {url}...");
                    var json = await converter.GenerateOpenApiFromWsdlAsync(url);
                    var uri = new Uri(url);
                    var firstSegment = uri.Segments.Length > 1 ? uri.Segments[1].Trim('/').Split('.').FirstOrDefault() ?? "schema" : "schema";
                    var outFile = Path.Combine(outDir.FullName, firstSegment + ".openapi.json");
                    await File.WriteAllTextAsync(outFile, json);

                    // compute md5
                    using var md5 = MD5.Create();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    var hash = BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

                    var existingHash = state.ContainsKey(outFile) ? state[outFile] : null;
                    var clientDir = Path.Combine(outDir.FullName, firstSegment + "_client");

                    var needKiota = existingHash != hash || !Directory.Exists(clientDir);
                    if (needKiota)
                    {
                        Console.WriteLine($"Running kiota for {outFile} -> {clientDir}");
                        // run kiota as external process (assumes kiota is available on PATH)
                        var psi = new System.Diagnostics.ProcessStartInfo("kiota", $"generate -l csharp -d \"{outFile}\" -o \"{clientDir}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
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
}