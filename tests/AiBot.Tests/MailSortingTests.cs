using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AiBot.Application;
using AiBot.Domain;
using AiBot.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AiBot.Tests;

internal static class MailSortingTests
{
    public static async Task RunMvcChecksAsync(TestApp factory, HttpClient client, Action<bool, string> check)
    {
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var original = await anonymous.GetAsync("/MailSorting/Original/" + Guid.NewGuid());
        check(original.StatusCode == HttpStatusCode.Redirect && original.Headers.Location?.OriginalString.Contains("/Account/Login") == true,
            "原始邮件下载要求登录");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("test"), "file", "test.eml");
        check((await client.PostAsync("/MailSorting/Import", form)).StatusCode == HttpStatusCode.BadRequest,
            "邮件导入要求防伪令牌");
        var home = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        check(new[] { "AI 邮件自动分拣", "AI 机器人", "AI 知识库", "系统配置", "账号管理" }.All(home.Contains),
            "中台导航按五个模块归类");
        var navigation = Regex.Match(home, """<nav class="module-navigation".*?</nav>""", RegexOptions.Singleline).Value;
        var robotOverview = navigation.IndexOf("控制台概览", StringComparison.Ordinal);
        check(robotOverview > navigation.IndexOf(">AI 机器人<", StringComparison.Ordinal) &&
            robotOverview < navigation.IndexOf(">AI 知识库<", StringComparison.Ordinal),
            "控制台概览位于 AI 机器人模块中");
    }

    public static void RunBookingInstructionChecks(Action<bool, string> check)
    {
        var fields = MailFieldCatalog.Fields.Where(definition => definition.Key != "cargo_handover").Select(definition => new MailField(definition.Key, definition.Label,
            definition.Key switch
            {
                "gross" => [new("110", "附件", "110kg")],
                "volume" => [new("0.70", "附件", "0.70CBM")],
                "trade" => [new("(Incoterm)：cif", "附件", "Incoterm：CIF")],
                "volume_weight" => [new("116.667", "原工作台解析", "自动计算 · 0.7 CBM")],
                _ => []
            })).ToArray();
        var original = new MailAnalysis("request", """
            直客预付货，上海示例，定自己CZ 直飞 PKX-ICN，11月16号航班，条款CFR，预计11/14号交北京，需要倒车，请今天发入仓图
            118kg 0.6cbm  5桶 注意 CZ 是窄体 不能打托。
            卖价 100kg: 8.5/kg
            客户要求：需要缠膜
            --完
            120kg 0.8cbm 6桶
            """, fields, [], "规则", []);
        var result = BookingInstructionRules.Enrich(original, "空运出口指令", new DateOnly(2026, 11, 10));
        MailField Field(string key) => result.Fields.Single(field => field.Key == key);
        string Value(string key) => Field(key).Candidates.Single().Value;
        check(Value("carrier") == "CZ" && Value("etd") == "2026-11-16" && Value("pieces") == "5" &&
            Value("packaging") == "桶" && Value("sell_rate") == "8.5" && Value("freight_payment") == "预付" &&
            Value("cargo_handover") == "2026-11-14 交北京" && Value("customer") == "上海示例" &&
            Value("handling").Contains("不能打托"), "订舱指令补提航司、日期、包装、运价和交货要求");
        check(Field("gross").HasConflict && Field("volume").HasConflict && Field("trade").HasConflict &&
            Field("gross").Candidates.Count == 2 && Field("trade").Candidates.Any(candidate => candidate.Value == "CFR") &&
            Field("volume_weight").Candidates.Count == 0, "订舱正文与附件冲突保留双方证据并隐藏有争议的自动计费重");
    }

    public static async Task RunConfiguredModelChecksAsync(Action<bool, string> check)
    {
        var repository = new MemoryAutomationRepository(new MemoryRepository());
        var endpoint = new AiModelEndpoint();
        endpoint.Update("测试模型", "测试提供方", "https://example.invalid/chat", "model-test", "chat", "fake-key", true, 5, 1024);
        repository.Add(endpoint);
        var client = new MailAiFake();
        var suggestions = new ConfiguredMailModel(repository, client);
        var baseline = new MailAnalysis("review", "不定期订舱件重 18 件", [
            new("pieces", "件数", []),
            new("gross", "毛重", [new("99", "规则", "毛重99")]),
            new("flight", "航班", []),
            new("mawb", "运单号", [], RequiresReview: true)
        ], [], "规则", []);
        var mail = new SortedMail(Guid.NewGuid(), "fingerprint", "测试案", "private-address@example.invalid",
            "导入", DateTimeOffset.UtcNow, 1, baseline, new("review", new() { ["flight"] = "AB789" }, false));
        var output = await suggestions.SuggestAsync(endpoint.Id, mail, baseline, null, default);
        check(client.ModelId == endpoint.Id && !client.Data.Contains(mail.Sender) &&
            output.Category == "request" && output.Fields[0].Candidates.Single().Value == "18" &&
            output.Fields[1].Candidates.Single().Value == "99" && output.Fields[2].Candidates.Count == 0 &&
            output.Fields[3].HasConflict && output.Fields[3].Candidates.Count == 0,
            "邮件模型复用所选接口，仅采纳有原文证据的缺失字段并保留规则和人工字段");
        client.Output = "bad response";
        var fallback = await suggestions.SuggestAsync(endpoint.Id, mail, baseline, "review", default);
        check(fallback.Engine.Contains("已采用规则结果") && fallback.Category == baseline.Category &&
            fallback.Fields.SequenceEqual(baseline.Fields), "邮件模型异常时保留规则结果");
    }

    private sealed class MailAiFake : IMailAiCompletionClient
    {
        public Guid ModelId { get; private set; }
        public string Data { get; private set; } = "";
        public string Output { get; set; } = """
            {"classification":{"category":"request","source":"body","quote":"不定期订舱"},
            "fields":[{"key":"pieces","value":"88","source":"body","quote":"不存在的内容"},
            {"key":"pieces","value":"18","source":"body","quote":"18 件"},
            {"key":"gross","value":"18","source":"body","quote":"18 件"},
            {"key":"flight","value":"18","source":"body","quote":"18 件"},
            {"key":"mawb","value":"18","source":"body","quote":"18 件"}]}
            """;
        public Task<string> CompleteMailAsync(AiModelEndpoint model, string prompt, string data, CancellationToken ct)
        {
            ModelId = model.Id;
            Data = data;
            return Task.FromResult(Output);
        }
    }

    public static async Task RunPostgresAsync(BotDbContext db, DbContextOptions<BotDbContext> dbOptions, Action<bool, string> check)
    {
        var options = Options.Create(new MailSortingOptions());
        using var http = new HttpClient();
        var model = new MailModelSuggestions(http, options);
        var sorting = new PostgresMailSorting(db, options, model);
        var raw = Eml("booking@sample.invalid", "Booking request PVG-LAX", """
            --mail-test
            Content-Type: text/plain; charset=UTF-8

            件数: 12
            毛重: 120
            体积: 1
            --mail-test
            Content-Type: text/plain; charset=UTF-8; name="packing.txt"
            Content-Disposition: attachment; filename="packing.txt"

            件数: 11
            --mail-test--
            """);
        var id = await sorting.ImportAsync(raw, "手动导入", "测试员", null, default);
        await using var anotherDb = new BotDbContext(dbOptions);
        var other = new PostgresMailSorting(anotherDb, options, model);
        check(await other.ImportAsync(raw, "试点邮箱", "自动收件", null, default) == id, "邮件导入与自动收件按 Message-ID 跨连接去重");
        var mail = (await sorting.FindAsync(id, default))!;
        check(mail.Analysis.Category == "request" && mail.Analysis.Fields.Single(x => x.Key == "origin").Candidates[0].Value == "PVG" &&
            mail.Analysis.Fields.Single(x => x.Key == "pieces").Candidates.Select(x => x.Value).SequenceEqual(new[] { "12", "11" }),
            "邮件主题与附件联合识别，保留件数冲突和候选来源");
        var attachment = await sorting.AttachmentAsync(id, 0, default);
        check(attachment?.Name == "packing.txt" && Encoding.UTF8.GetString(attachment.Value.Data).Contains("11") &&
            (await sorting.OriginalAsync(id, default))!.SequenceEqual(raw), "邮件原件及附件按原内容下载");
        check(await sorting.ReviewAsync(mail, new MailReview("request", new() { ["pieces"] = "11" }, true), "测试员", default) &&
            !await other.ReviewAsync(mail, new MailReview("documents", [], false), "另一个操作员", default),
            "跨连接的旧版本复核提交不能覆盖已有修改");
        var reviewed = (await sorting.FindAsync(id, default))!;
        check(await sorting.ReanalyzeAsync(reviewed, "测试员", default), "已复核邮件可以重新识别");
        var reanalyzed = (await sorting.FindAsync(id, default))!;
        check(reanalyzed.Review?.Fields["pieces"] == "11" && reanalyzed.Review.Confirmed &&
            (await sorting.AuditAsync(id, default)).Select(x => x.Action).SequenceEqual(new[] { "重新识别", "确认复核", "收件" }),
            "重新识别保留人工确认值并记录操作人");
        var listed = (await sorting.ListAsync("pvg", "request", "true", 1, default)).Single();
        check(listed.Summary["origin"] == "PVG" && listed.Summary["destination"] == "LAX" && listed.Summary["pieces"] == "11",
            "待办列表显示航线及人工确认的字段摘要");
        check(listed.Id == id,
            "邮件待办按主题、分类和状态筛选");

        var reply = Encoding.UTF8.GetBytes("""
            From: agent@sample.invalid
            To: booking@sample.invalid
            Subject: Re: Booking request PVG-LAX
            Message-Id: <confirmed@sample.invalid>
            In-Reply-To: <booking@sample.invalid>
            Content-Type: text/plain; charset=UTF-8

            订舱确认
            航班号: XX123
            """.Replace("\n", "\r\n"));
        var followUp = (await sorting.FindAsync(await sorting.ImportAsync(reply, "测试", "测试员", null, default), default))!;
        check(followUp.Analysis.Category == "confirmation" &&
            followUp.Analysis.Fields.Single(x => x.Key == "gross").Candidates.Any(x => x.Value == "120" && x.Source.Contains("历史往来")),
            "通过回复头关联已收往来邮件并补齐缺失字段");

        var secrets = new SecretProtector(new EphemeralDataProtectionProvider());
        var credentials = new PostgresMailboxCredentials(db, secrets);
        var mailbox = SortingMailboxIdentity.Key(new() { Host = "imap.sample.invalid", Username = "reviewer" });
        await credentials.SavePasswordAsync(mailbox, "test-only-password", default);
        await using var command = new NpgsqlCommand("SELECT protected_password FROM ai_bot.mail_credentials", (NpgsqlConnection)db.Database.GetDbConnection());
        var encrypted = (string)(await command.ExecuteScalarAsync())!;
        check(await credentials.HasPasswordAsync(mailbox, default) && !encrypted.Contains("test-only-password") &&
            secrets.Unprotect(encrypted) == "test-only-password", "邮箱授权码沿用 AIBot 凭据加密和密钥环");
        var source = new LegacyMailRuleSource(options, Host.CreateApplicationBuilder().Environment);
        var ruleSettings = new PostgresMailRuleSettings(db, source, options);
        await CheckMvcImportAsync(sorting, new PostgresMailboxSettings(db, secrets), ruleSettings, raw, check);
        await CheckLegacyAnalyzerAsync(db, check);
    }

    private static async Task CheckMvcImportAsync(IMailSorting sorting, IMailboxSettings mailboxSettings, IMailRuleSettings ruleSettings, byte[] raw, Action<bool, string> check)
    {
        await using var app = new TestApp();
        await using var isolated = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMailSorting>();
            services.AddSingleton(sorting);
            services.RemoveAll<IMailboxSettings>();
            services.AddSingleton(mailboxSettings);
            services.RemoveAll<IMailRuleSettings>();
            services.AddSingleton(ruleSettings);
        }));
        using var client = isolated.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

        var login = await client.GetStringAsync("/Account/Login");
        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "reviewer", ["Password"] = "PreviewOnly!2026", ["__RequestVerificationToken"] = Token(login)
        });
        using var authenticated = await client.PostAsync("/Account/Login", loginForm);
        var editor = await client.GetStringAsync("/MailSorting/Rules");
        var ruleForm = new Dictionary<string, string>
        {
            ["Revision"] = "0", ["VolumeDivisor"] = "0", ["Keywords[request]"] = "MAIL RULE EDIT",
            ["Defaults[salesperson]"] = "页面销售", ["Routes"] = "route@example.invalid = 文件检查",
            ["Customers"] = "sender@sample.invalid = 页面客户", ["__RequestVerificationToken"] = Token(editor)
        };
        using var invalidForm = new FormUrlEncodedContent(ruleForm);
        using var invalidRules = await client.PostAsync("/MailSorting/Rules", invalidForm);
        check(invalidRules.StatusCode == HttpStatusCode.OK && (await ruleSettings.ReadAsync(default)).Revision == 0,
            "编辑页面拒绝无效体积重除数并保留已保存设置");
        ruleForm["VolumeDivisor"] = "5100";
        using var editableForm = new FormUrlEncodedContent(ruleForm);
        using var savedRules = await client.PostAsync("/MailSorting/Rules", editableForm);
        var currentRules = await ruleSettings.ReadAsync(default);
        check(savedRules.StatusCode == HttpStatusCode.Redirect && currentRules.Revision == 1 &&
            currentRules.Snapshot.VolumeDivisor == 5100 && currentRules.Snapshot.Keywords["request"].Single() == "MAIL RULE EDIT" &&
            currentRules.Snapshot.Customers["sender@sample.invalid"] == "页面客户" &&
            currentRules.Snapshot.Routes["route@example.invalid"] == "documents",
            "编辑页面将关键词、体积重、收件路由和客户映射持久化");
        ruleForm["VolumeDivisor"] = "5200";
        using var staleForm = new FormUrlEncodedContent(ruleForm);
        using var staleRules = await client.PostAsync("/MailSorting/Rules", staleForm);
        check(staleRules.StatusCode == HttpStatusCode.OK && (await ruleSettings.ReadAsync(default)).Snapshot.VolumeDivisor == 5100 &&
            WebUtility.HtmlDecode(await staleRules.Content.ReadAsStringAsync()).Contains("规则已被其他操作更新"),
            "规则编辑拒绝覆盖其他页面已保存的修改");

        var boxes = WebUtility.HtmlDecode(await client.GetStringAsync("/MailSorting/Mailboxes"));
        check(boxes.Contains("新增邮箱"), "收件邮箱列表提供新增入口");
        var edit = await client.GetStringAsync("/MailSorting/EditMailbox");
        using var mailbox = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "本地试点", ["Protocol"] = "pop3", ["Host"] = "pop.sample.invalid", ["Username"] = "reviewer",
            ["Port"] = "0", ["Enabled"] = "false", ["Password"] = "test-only-mail-secret",
            ["__RequestVerificationToken"] = Token(edit)
        });
        using var savedMailbox = await client.PostAsync("/MailSorting/EditMailbox", mailbox);
        var managed = (await mailboxSettings.ListAsync(default)).Single();
        var existingMailbox = await client.GetStringAsync("/MailSorting/EditMailbox/" + managed.Id);
        check(savedMailbox.StatusCode == HttpStatusCode.Redirect && managed.HasPassword && !managed.Enabled &&
            !existingMailbox.Contains("test-only-mail-secret"), "页面可新增 POP3 监测邮箱，并加密保存且不回显授权码");
        await mailboxSettings.SaveAsync(managed, default);
        check((await mailboxSettings.ListAsync(default)).Single().HasPassword, "编辑邮箱时留空保留授权码");

        var index = await client.GetStringAsync("/MailSorting");
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(raw), "file", "booking.eml");
        upload.Add(new StringContent(Token(index)), "__RequestVerificationToken");
        using var imported = await client.PostAsync("/MailSorting/Import", upload);
        check(imported.StatusCode == HttpStatusCode.Redirect && imported.Headers.Location?.OriginalString.Contains("/MailSorting/Details") == true,
            "登录后可通过中台页面导入 .eml");
        var detailsUri = imported.Headers.Location!;
        var details = await client.GetStringAsync(detailsUri);
        check(WebUtility.HtmlDecode(details).Contains("字段冲突") && details.Contains("packing.txt"),
            "分拣详情显示真实字段冲突和附件");
        var rendered = WebUtility.HtmlDecode(details);
        var requiredAt = rendered.IndexOf("待补录的必填字段", StringComparison.Ordinal);
        var readyAt = rendered.IndexOf("已识别或已补录的必填字段", StringComparison.Ordinal);
        var optionalAt = rendered.IndexOf("选填字段", StringComparison.Ordinal);
        var missingSalespersonAt = rendered.IndexOf("id=\"mail-field-salesperson\"", StringComparison.Ordinal);
        var optionalReferenceAt = rendered.IndexOf("id=\"mail-field-reference\"", StringComparison.Ordinal);
        check(requiredAt >= 0 && missingSalespersonAt > requiredAt && readyAt > missingSalespersonAt &&
            optionalAt > readyAt && optionalReferenceAt > optionalAt, "订舱申请先显示待补必填字段，已填必填和选填字段分别展示");
        var id = detailsUri.OriginalString.Split('/').Last();
        var revision = Regex.Match(details, "name=\"revision\" value=\"([0-9]+)\"").Groups[1].Value;
        using var review = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id, ["revision"] = revision, ["category"] = "request", ["confirmed"] = "true",
            ["fields[pieces]"] = "13", ["__RequestVerificationToken"] = Token(details)
        });
        using var saved = await client.PostAsync("/MailSorting/Save", review);
        check(saved.StatusCode == HttpStatusCode.Redirect &&
            (await sorting.FindAsync(Guid.Parse(id), default))?.Review?.Fields["pieces"] == "13",
            "中台复核表单可保存人工确认字段");
    }

    private static async Task CheckLegacyAnalyzerAsync(BotDbContext db, Action<bool, string> check)
    {
        var python = Environment.GetEnvironmentVariable("AIBOT_VERIFY_PYTHON_EXECUTABLE");
        var parser = Environment.GetEnvironmentVariable("AIBOT_VERIFY_PYTHON_PARSER");
        if (string.IsNullOrEmpty(python) || string.IsNullOrEmpty(parser)) return;
        var settings = Options.Create(new MailSortingOptions { PythonExecutable = python, PythonParserDirectory = parser });
        var analyzer = new LegacyMailAnalyzer(settings, NullLogger<LegacyMailAnalyzer>.Instance);
        using var http = new HttpClient();
        var sorting = new PostgresMailSorting(db, settings, new MailModelSuggestions(http, settings), analyzer);
        var raw = Eml("legacy-booking@sample.invalid", "Booking request PVG-SIN", """
            --mail-test
            Content-Type: text/plain; charset=UTF-8

            件数：12
            --mail-test
            Content-Type: text/csv; charset=UTF-8; name="packing.csv"
            Content-Disposition: attachment; filename="packing.csv"

            件数(PCS),毛重(KGS),体积(CBM),中文品名
            11,186.5,0.84,机械配件
            --mail-test--
            """);
        var id = await sorting.ImportAsync(raw, "样本", "测试员", null, default);
        var analysis = (await sorting.FindAsync(id, default))!.Analysis;
        check(analysis.Engine.Contains("原工作台解析器") &&
            analysis.Fields.Single(f => f.Key == "commodity_cn").Candidates.Any(c => c.Value == "机械配件") &&
            analysis.Fields.Single(f => f.Key == "gross").Candidates.Any(c => c.Value == "186.5") &&
            analysis.Fields.Single(f => f.Key == "pieces").HasConflict,
            "原工作台解析器恢复装箱单 CSV 的毛重、品名和正文与附件的件数冲突");
        await CheckSyncedRulesAsync(db, python, parser, check);
    }

    private static async Task CheckSyncedRulesAsync(BotDbContext db, string python, string parser, Action<bool, string> check)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """
                {"version":7,"volume_divisor":5000,"keywords":{"request":["SPECIAL BOOKING"],"confirmation":[],"documents":[],"exception":[]},
                "defaults":{"salesperson":"测试销售"},"routes":{"routed@sample.invalid":"documents"},
                "customer_rules":{"sender@sample.invalid":"映射客户"}}
                """);
            var settings = Options.Create(new MailSortingOptions
            {
                PythonExecutable = python, PythonParserDirectory = parser, RulesSnapshot = path
            });
            var rules = new LegacyMailRuleSource(settings, Host.CreateApplicationBuilder().Environment);
            var analyzer = new LegacyMailAnalyzer(settings, NullLogger<LegacyMailAnalyzer>.Instance, rules);
            using var http = new HttpClient();
            var sorting = new PostgresMailSorting(db, settings, new MailModelSuggestions(http, settings), analyzer, rules);
            var raw = Encoding.UTF8.GetBytes("""
                From: sender@sample.invalid
                To: booking@sample.invalid
                Subject: SPECIAL BOOKING
                Message-Id: <sync-keywords@sample.invalid>
                Content-Type: text/plain; charset=UTF-8

                毛重: 110
                体积: 1 CBM
                """.Replace("\n", "\r\n"));
            var id = await sorting.ImportAsync(raw, "样本", "测试员", null, default);
            var result = (await sorting.FindAsync(id, default))!.Analysis;
            string? Value(string key) => result.Fields.Single(f => f.Key == key).Candidates.FirstOrDefault()?.Value;
            check(result.Category == "request" && Value("customer") == "映射客户" &&
                Value("salesperson") == "测试销售" && Value("volume_weight") == "200" && Value("settlement") is null,
                "同步的关键词、客户映射、默认值和体积重除数由原解析器实际使用");

            var routed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw)
                .Replace("To: booking@sample.invalid", "To: booking@sample.invalid\r\nDelivered-To: routed@sample.invalid")
                .Replace("sync-keywords@", "sync-route@"));
            var routedId = await sorting.ImportAsync(routed, "样本", "测试员", null, default);
            check((await sorting.FindAsync(routedId, default))?.Analysis.Category == "documents",
                "同步的收件地址路由优先使用信封投递地址");

            var persisted = new PostgresMailRuleSettings(db, rules, settings);
            var live = new PostgresMailSorting(db, settings, new MailModelSuggestions(http, settings), analyzer, rules,
                ruleSettings: persisted);
            var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw)
                .Replace("SPECIAL BOOKING", "MAIL RULE EDIT").Replace("sync-keywords@", "edited-rule@"));
            var savedId = await live.ImportAsync(edited, "样本", "测试员", null, default);
            var active = (await live.FindAsync(savedId, default))!.Analysis;
            check(active.Category == "request" && active.Engine.Contains("原工作台解析器") &&
                active.Fields.Single(x => x.Key == "customer").Candidates.Single().Value == "页面客户" &&
                active.Fields.Single(x => x.Key == "salesperson").Candidates.Single().Value == "页面销售",
                "原 Python 解析器即时使用页面保存的规则，优先于原导入快照");
        }
        finally { File.Delete(path); }
    }

    private static byte[] Eml(string messageId, string subject, string body) => Encoding.UTF8.GetBytes($"""
        From: sender@sample.invalid
        To: booking@sample.invalid
        Subject: {subject}
        Message-Id: <{messageId}>
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="mail-test"

        {body}
        """.Replace("\n", "\r\n"));
}
