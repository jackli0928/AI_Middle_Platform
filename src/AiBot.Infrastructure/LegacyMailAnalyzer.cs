using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiBot.Application;
using AiBot.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiBot.Infrastructure;

public sealed class LegacyMailAnalyzer(IOptions<MailSortingOptions> options, ILogger<LegacyMailAnalyzer> logger, LegacyMailRuleSource? rules = null)
{
    public async Task<MailAnalysis?> AnalyzeAsync(byte[] raw, MailAnalysis native, string? category, CancellationToken ct, IReadOnlyList<byte[]>? related = null, MailRuleSnapshot? snapshot = null, bool allowOllama = true)
    {
        var configuration = options.Value;
        var python = configuration.PythonExecutable;
        var source = configuration.PythonParserDirectory;
        if (!Path.IsPathFullyQualified(python) || !Path.IsPathFullyQualified(source)) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo
        {
            FileName = python, WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-I", "-B", "-X", "utf8", "-c", Parser, source, category ?? "",
            rules?.Path ?? "", allowOllama ? configuration.OllamaModel : "", Math.Clamp(configuration.VolumeDivisor, 1, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var rulesData = snapshot is null ? [] : JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await WriteMailAsync(process.StandardInput.BaseStream, rulesData, timeout.Token);
            await WriteMailAsync(process.StandardInput.BaseStream, raw, timeout.Token);
            if (related is not null)
                foreach (var earlier in related)
                    await WriteMailAsync(process.StandardInput.BaseStream, earlier, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            if (process.ExitCode != 0) throw new InvalidDataException("Python parser failed.");
            var result = JsonSerializer.Deserialize<ParserResult>(await output, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Empty parser output.");
            if (!MailSorting.Categories.ContainsKey(result.Category)) throw new InvalidDataException("Unknown category.");
            return native with
            {
                Category = result.Category,
                Fields = native.Fields.Select(field =>
                {
                    var evidence = result.Evidence.GetValueOrDefault(field.Key) ?? "";
                    if (result.Conflicts.Contains(field.Key))
                        return field with
                        {
                            RequiresReview = true, ReviewEvidence = evidence
                        };
                    if (result.Fields.TryGetValue(field.Key, out var value))
                        return field with { Candidates = [new(value, "原工作台解析", evidence)] };
                    return field with { Candidates = field.Candidates.Where(c => c.Source.StartsWith("历史往来", StringComparison.Ordinal)).ToArray() };
                }).ToArray(),
                Engine = "原工作台解析器 · " + result.Engine
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is
            (Win32Exception or IOException or InvalidOperationException or JsonException or OperationCanceledException or InvalidDataException))
        {
            logger.LogWarning("邮件原解析器不可用（{Type}），本封邮件回退到 .NET 识别。", ex.GetType().Name);
            return null;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    private static async Task WriteMailAsync(Stream stream, byte[] raw, CancellationToken ct)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, raw.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(raw, ct);
    }

    private sealed record ParserResult(string Category, Dictionary<string, string> Fields,
        Dictionary<string, string> Evidence, HashSet<string> Conflicts, string Engine);

    private const string Parser = """
        import json, struct, sys
        from copy import deepcopy
        from pathlib import Path

        root, fixed, rules, model, volume_divisor = sys.argv[1:]
        sys.path[:0] = [root, str(Path(root) / ".vendor")]
        from intake import MAX_MAIL, propose, read_mail
        from booking import fill_parties, fill_prices, fill_weights
        from customers import customer_options
        from orders import DEFAULT_CONFIG

        def read_frame(allow_empty=False):
            header = sys.stdin.buffer.read(4)
            if not header:
                return None
            size, = struct.unpack("<I", header)
            if not (0 if allow_empty else 1) <= size <= MAX_MAIL:
                raise ValueError("invalid mail size")
            data = sys.stdin.buffer.read(size)
            if len(data) != size:
                raise ValueError("truncated mail")
            return data

        def read_message():
            data = read_frame()
            return read_mail(data) if data is not None else None

        active_settings = read_frame(allow_empty=True)
        mail = read_message()
        related = []
        while (earlier := read_message()) is not None:
            related.append(earlier)
        config = deepcopy(DEFAULT_CONFIG)
        config["volume_divisor"] = int(volume_divisor)
        if active_settings:
            config.update(json.loads(active_settings))
        elif rules:
            config.update(json.loads(Path(rules).read_text(encoding="utf-8")))
        category, fields, evidence, conflicts, engine = propose(mail, config, model, fixed or None, related)
        if category in ("request", "documents"):
            fill_parties(fields, evidence, conflicts)
        if category == "request":
            if not fields.get("customer") and "customer" not in conflicts:
                strong = [item for item in customer_options(mail, config, fields) if item["strong"]]
                if len(strong) == 1:
                    fields["customer"], evidence["customer"] = strong[0]["name"], strong[0]["source"]
                elif strong:
                    conflicts.append("customer")
                    evidence["customer"] = "\n".join(f"{item['name']} · {item['source']}" for item in strong)
            fill_prices(fields, evidence, conflicts)
            fill_weights(fields, evidence, conflicts, config["volume_divisor"])
            for key, value in config["defaults"].items():
                if key not in fields and key not in conflicts:
                    fields[key] = value
                    evidence[key] = "流程默认值"
        fields["service"] = "空运出口"
        print(json.dumps(dict(category=category, fields=fields, evidence=evidence, conflicts=conflicts, engine=engine)))
        """;
}
