using BookSearch.Models.Management;

/// <summary>`bookcli models ...`: choose, download, verify and import the models (ST-09).</summary>
internal static class ModelCommands
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0) return Usage();
        var command = args[0];
        var data = Option(args, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookSearch");
        var manifestPath = Option(args, "--manifest");
        var manifest = manifestPath is null ? ModelManifest.LoadDefault() : ModelManifest.Load(manifestPath);
        var store = new ModelStore(Path.Combine(data, "models"));
        var ramGb = Option(args, "--ram-gb") is { } r && double.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g)
            ? (long)(g * 1024 * 1024 * 1024) : HardwareTiers.DetectTotalRamBytes();
        var tier = Option(args, "--tier") is { } t && Enum.TryParse<ModelTier>(t, true, out var chosen) ? chosen : HardwareTiers.Select(ramGb);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return command switch
            {
                "list" => await List(manifest, store, ramGb, tier, cts.Token),
                "download" => await Download(manifest, store, tier, Option(args, "--only"), cts.Token),
                "verify" => await Verify(manifest, store, cts.Token),
                "import" => await Import(args.Skip(1).Where(a => !a.StartsWith("--") && !IsOptionValue(args, a)).ToList(), manifest, store, cts.Token),
                _ => Usage(),
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nStopped. Run the same command again to carry on from where it left off.");
            return 130;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: bookcli models list|download|verify|import <files...>");
        Console.Error.WriteLine("       options: --data <folder>  --tier minimum|recommended  --only embedding|llm  --manifest <file>  --ram-gb <n>");
        return 2;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool IsOptionValue(string[] args, string value)
    {
        var i = Array.IndexOf(args, value);
        return i > 0 && args[i - 1].StartsWith("--");
    }

    private static string Size(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F2} GB";

    private static async Task<int> List(ModelManifest manifest, ModelStore store, long ramBytes, ModelTier tier, CancellationToken ct)
    {
        Console.WriteLine($"Memory: {HardwareTiers.ToGb(ramBytes):F1} GB -> {tier} tier ({manifest.LlmFor(tier).Id})" +
                          (HardwareTiers.IsBelowMinimum(ramBytes) ? "   Warning: below the 8 GB minimum; Summarize may be very slow." : ""));
        Console.WriteLine($"Models folder: {store.Folder}\n");
        var selected = manifest.ForTier(tier).Select(e => e.Id).ToHashSet();
        foreach (var e in manifest.Entries.Where(e => e.Role != ModelRole.Runtime))   // the llama.cpp zip is handled by the app, not this tool
        {
            var state = await store.CheckAsync(e, ct);
            var detail = state == ModelState.Partial
                ? $" ({new FileInfo(store.PartPathFor(e)).Length * 100 / e.SizeBytes}% downloaded)" : "";
            Console.WriteLine($"  {e.Id,-28} {e.Role,-9} {(e.Tier?.ToString().ToLowerInvariant() ?? "all"),-12} {Size(e.SizeBytes),8}  {e.License,-10} " +
                              $"{state.ToString().ToLowerInvariant()}{detail}{(selected.Contains(e.Id) ? "   <- needed on this machine" : "")}");
        }
        return 0;
    }

    private static async Task<int> Download(ModelManifest manifest, ModelStore store, ModelTier tier, string? only, CancellationToken ct)
    {
        var entries = manifest.ForTier(tier)
            .Where(e => only is null || e.Role.ToString().Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0) return Usage();

        var downloader = new ModelDownloader();
        var failures = 0;
        foreach (var entry in entries)
        {
            Console.WriteLine($"{entry.Id} ({Size(entry.SizeBytes)}) from {new Uri(entry.Url).Host}");
            var started = DateTime.UtcNow;
            var progress = new Progress<DownloadProgress>(p =>
            {
                var mb = p.BytesDone / (1024.0 * 1024);
                var total = p.BytesTotal / (1024.0 * 1024);
                var rate = p.Verifying ? "" : $"  {mb / Math.Max(0.1, (DateTime.UtcNow - started).TotalSeconds):F1} MB/s average";
                Console.Write($"\r  {(p.Verifying ? "checking checksum" : "downloading")} {mb:F0} / {total:F0} MB ({100 * p.BytesDone / Math.Max(1, p.BytesTotal)}%){rate}      ");
            });
            try
            {
                await downloader.DownloadAsync(entry, store, progress, ct);
                Console.WriteLine($"\r  installed, checksum verified{new string(' ', 60)}");
            }
            catch (ChecksumMismatchException e)
            {
                Console.WriteLine($"\r  CHECKSUM ERROR: {e.Message}\n  The file was deleted. Run the command again to download it fresh.");
                failures++;
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                Console.WriteLine($"\r  stopped: {e.Message}\n  What arrived is kept; run the command again to resume.");
                failures++;
            }
        }

        if (failures == 0)
        {
            Console.WriteLine($"\nDone. Add these to config.json (and set serverExe to llama-server):");
            foreach (var e in entries)
                Console.WriteLine($"  \"{(e.Role == ModelRole.Embedding ? "embeddingModel" : "llmModel")}\": \"{store.PathFor(e).Replace("\\", "\\\\")}\"");
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> Verify(ModelManifest manifest, ModelStore store, CancellationToken ct)
    {
        var bad = 0;
        foreach (var e in manifest.Entries)
        {
            Console.Write($"  {e.Id,-28} checking... ");
            var state = await store.CheckAsync(e, ct);
            Console.WriteLine(state switch
            {
                ModelState.Installed => "ok",
                ModelState.Missing => "not installed",
                ModelState.Partial => "incomplete download (run `models download` to resume)",
                _ => "CHECKSUM ERROR: the file doesn't match the model list. Run `models download` to fetch it again.",
            });
            if (state == ModelState.Corrupt) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    private static async Task<int> Import(List<string> files, ModelManifest manifest, ModelStore store, CancellationToken ct)
    {
        if (files.Count == 0) return Usage();
        var failures = 0;
        foreach (var file in files)
        {
            try
            {
                var entry = await ModelImporter.ImportAsync(file, manifest, store, null, ct);
                Console.WriteLine($"  {Path.GetFileName(file)}: imported as {entry.Id}");
            }
            catch (Exception e) when (e is ModelRejectedException or FileNotFoundException)
            {
                Console.WriteLine($"  {Path.GetFileName(file)}: rejected. {e.Message}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
