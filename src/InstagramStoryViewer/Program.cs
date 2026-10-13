using System.Diagnostics;
using System.Text;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;
using InstagramStoryViewer.Services;

namespace InstagramStoryViewer;

public static class Program
{
    private const string SessionFile = "session.txt";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var cookies = LoadSession();
            using var client = new InstagramClient(cookies);
            var app = new App(client, new StoryDownloader(client, options.OutputFolder));

            if (options.Tray)
                await app.ShowTrayAsync(interactive: false, cts.Token);
            else if (options.Username is not null)
                await app.ShowUserStoriesAsync(options.Username, options.Download, options.MarkSeen, interactive: false, cts.Token);
            else
                await app.RunInteractiveAsync(cts.Token);

            return 0;
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("تم الإلغاء.");
            return 130;
        }
        catch (InstagramApiException ex)
        {
            Ui.Error(ex.Message);
            if (ex.Kind == InstagramErrorKind.LoginRequired && File.Exists(SessionFile))
                Ui.Info($"احذف الملف {SessionFile} أو حدّثه بجلسة جديدة.");
            return 1;
        }
        catch (ArgumentException ex)
        {
            Ui.Error(ex.Message);
            return 2;
        }
    }

    /// <summary>Order: IG_COOKIE env var → session.txt → interactive prompt.</summary>
    private static SessionCookies LoadSession()
    {
        var env = Environment.GetEnvironmentVariable("IG_COOKIE") ?? Environment.GetEnvironmentVariable("IG_SESSIONID");
        if (!string.IsNullOrWhiteSpace(env))
            return SessionCookies.Parse(env);

        if (File.Exists(SessionFile))
        {
            var saved = File.ReadAllText(SessionFile).Trim();
            if (saved.Length > 0) return SessionCookies.Parse(saved);
        }

        Ui.Title("تسجيل الدخول");
        Console.WriteLine("1) افتح instagram.com في المتصفح وسجّل الدخول.");
        Console.WriteLine("2) اضغط F12 ← Application/Storage ← Cookies ← https://www.instagram.com");
        Console.WriteLine("3) انسخ قيمة sessionid (ويُفضَّل أيضاً csrftoken بصيغة: sessionid=...; csrftoken=...)");
        Console.WriteLine();
        Console.Write("الصق الكوكيز هنا: ");
        var input = Ui.ReadSecret();
        var cookies = SessionCookies.Parse(input);

        if (Ui.Confirm($"حفظ الجلسة في {SessionFile} لاستخدامها لاحقاً؟ (لا تشارك هذا الملف مع أحد)"))
            File.WriteAllText(SessionFile, input.Trim());

        return cookies;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Instagram Story Viewer — عارض ستوريات إنستجرام

            الاستخدام:
              InstagramStoryViewer                         وضع تفاعلي (قائمة)
              InstagramStoryViewer --tray                  عرض ستوريات الحسابات التي تتابعها
              InstagramStoryViewer --user <username>       عرض ستوريات مستخدم
                    [--download]                           تنزيل الستوريات
                    [--seen]                               تعليمها كمشاهَدة (يظهر اسمك لصاحبها)
                    [--out <folder>]                       مجلد التنزيل (الافتراضي: stories)

            الجلسة تُقرأ من متغيّر البيئة IG_COOKIE أو من الملف session.txt، وإلا يُطلب إدخالها.
            """);
    }
}

internal sealed class CliOptions
{
    public string? Username { get; private set; }
    public bool Tray { get; private set; }
    public bool Download { get; private set; }
    public bool MarkSeen { get; private set; }
    public bool ShowHelp { get; private set; }
    public string OutputFolder { get; private set; } = "stories";

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-u" or "--user":
                    o.Username = Next(args, ref i);
                    break;
                case "-t" or "--tray":
                    o.Tray = true;
                    break;
                case "-d" or "--download":
                    o.Download = true;
                    break;
                case "-s" or "--seen":
                    o.MarkSeen = true;
                    break;
                case "-o" or "--out":
                    o.OutputFolder = Next(args, ref i);
                    break;
                case "-h" or "--help" or "/?":
                    o.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"خيار غير معروف: {args[i]} (استخدم --help)");
            }
        }
        return o;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"الخيار {args[i]} يحتاج قيمة.");
        return args[++i];
    }
}

internal sealed class App
{
    private readonly InstagramClient _client;
    private readonly StoryDownloader _downloader;

    public App(InstagramClient client, StoryDownloader downloader)
    {
        _client = client;
        _downloader = downloader;
    }

    public async Task RunInteractiveAsync(CancellationToken ct)
    {
        while (true)
        {
            Ui.Title("Instagram Story Viewer");
            Console.WriteLine("  1) ستوريات الحسابات التي أتابعها");
            Console.WriteLine("  2) عرض ستوريات مستخدم معيّن");
            Console.WriteLine("  3) تنزيل ستوريات مستخدم معيّن");
            Console.WriteLine("  0) خروج");
            var choice = Ui.Prompt("اختيارك");

            try
            {
                switch (choice)
                {
                    case "1":
                        await ShowTrayAsync(interactive: true, ct);
                        break;
                    case "2":
                    case "3":
                        var username = Ui.Prompt("اسم المستخدم");
                        if (username.Length > 0)
                            await ShowUserStoriesAsync(username, download: choice == "3", markSeen: false, interactive: choice == "2", ct);
                        break;
                    case "0":
                    case "":
                        return;
                }
            }
            catch (InstagramApiException ex) when (ex.Kind is not InstagramErrorKind.LoginRequired and not InstagramErrorKind.Checkpoint)
            {
                // Recoverable errors: report and go back to the menu. Session errors bubble up and exit.
                Ui.Error(ex.Message);
            }
        }
    }

    public async Task ShowTrayAsync(bool interactive, CancellationToken ct)
    {
        Ui.Info("جاري جلب الستوريات...");
        var tray = await _client.GetStoriesTrayAsync(ct);
        if (tray.Count == 0)
        {
            Ui.Warn("لا توجد ستوريات حالياً من الحسابات التي تتابعها.");
            return;
        }

        Ui.Title($"الستوريات المتاحة ({tray.Count})");
        for (var i = 0; i < tray.Count; i++)
        {
            var r = tray[i];
            var marker = r.HasUnseen ? "●" : "○";
            var count = r.MediaCount ?? r.Items?.Count;
            var latest = r.LatestReelMedia is > 0
                ? Ui.Ago(DateTimeOffset.FromUnixTimeSeconds(r.LatestReelMedia.Value))
                : "";
            Console.ForegroundColor = r.HasUnseen ? ConsoleColor.Magenta : ConsoleColor.Gray;
            Console.WriteLine($"  {i + 1,3}) {marker} @{r.User!.Username,-30} {(count is null ? "" : $"{count} ستوري")}  {latest}");
        }
        Console.ResetColor();
        Console.WriteLine("  (● = لم تُشاهَد بعد)");

        if (!interactive) return;

        while (true)
        {
            var pick = Ui.Prompt("رقم الحساب لعرض ستورياته (Enter للرجوع)");
            if (pick.Length == 0) return;
            if (int.TryParse(pick, out var n) && n >= 1 && n <= tray.Count)
            {
                var user = tray[n - 1].User!;
                await ShowReelAsync(user.Username!, user.Pk!, download: false, markSeen: false, interactive: true, ct);
            }
            else Ui.Warn("رقم غير صالح.");
        }
    }

    public async Task ShowUserStoriesAsync(string username, bool download, bool markSeen, bool interactive, CancellationToken ct)
    {
        Ui.Info($"جاري البحث عن @{username.TrimStart('@')}...");
        var profile = await _client.GetProfileAsync(username, ct);
        if (profile.IsPrivate && !profile.FollowedByViewer && profile.Id != _client.ViewerId)
        {
            Ui.Warn($"@{profile.Username} حساب خاص ولا تتابعه، لا يمكن عرض ستورياته.");
            return;
        }
        await ShowReelAsync(profile.Username ?? username, profile.Id!, download, markSeen, interactive, ct);
    }

    private async Task ShowReelAsync(string username, string userId, bool download, bool markSeen, bool interactive, CancellationToken ct)
    {
        var reel = await _client.GetUserStoriesAsync(userId, ct);
        if (reel?.Items is null)
        {
            Ui.Warn($"لا توجد ستوريات حالية لـ @{username}.");
            return;
        }

        var items = reel.Items;
        PrintItems(username, items);

        if (download) await DownloadAllAsync(username, items, ct);
        if (markSeen) await MarkSeenAsync(userId, items, ct);
        if (!interactive) return;

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("  [رقم] فتح الستوري في المتصفح   d) تنزيل الكل   s) تعليم الكل كمشاهَد   l) إعادة العرض   Enter) رجوع");
            var cmd = Ui.Prompt(">").ToLowerInvariant();
            switch (cmd)
            {
                case "":
                    return;
                case "d":
                    await DownloadAllAsync(username, items, ct);
                    break;
                case "s":
                    if (Ui.Confirm($"سيظهر اسمك في قائمة المشاهدين لدى @{username}. متابعة؟"))
                        await MarkSeenAsync(userId, items, ct);
                    break;
                case "l":
                    PrintItems(username, items);
                    break;
                default:
                    if (int.TryParse(cmd, out var n) && n >= 1 && n <= items.Count)
                        Ui.OpenUrl(items[n - 1].BestUrl);
                    else
                        Ui.Warn("أمر غير معروف.");
                    break;
            }
        }
    }

    private static void PrintItems(string username, List<StoryItem> items)
    {
        Ui.Title($"ستوريات @{username} ({items.Count})");
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var type = it.IsVideo ? $"🎬 فيديو {it.VideoDuration ?? 0:0.#}ث" : "🖼  صورة";
            var left = it.ExpiresAtTime - DateTimeOffset.UtcNow;
            Console.WriteLine($"  {i + 1,3}) {type,-14} نُشر {Ui.Ago(it.TakenAtTime),-16} ينتهي بعد {Ui.Duration(left)}");
        }
    }

    private async Task DownloadAllAsync(string username, List<StoryItem> items, CancellationToken ct)
    {
        var ok = 0;
        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                var path = await _downloader.DownloadAsync(username, items[i], ct);
                ok++;
                Ui.Success($"  [{i + 1}/{items.Count}] {path}");
            }
            catch (InstagramApiException ex)
            {
                Ui.Error($"  [{i + 1}/{items.Count}] {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                Ui.Error($"  [{i + 1}/{items.Count}] خطأ في الشبكة: {ex.Message}");
            }
        }
        Ui.Info($"تم تنزيل {ok} من {items.Count}.");
    }

    private async Task MarkSeenAsync(string userId, List<StoryItem> items, CancellationToken ct)
    {
        var ok = 0;
        foreach (var item in items)
        {
            try
            {
                await _client.MarkAsSeenAsync(item, userId, ct);
                ok++;
            }
            catch (InstagramApiException ex) when (ex.Kind is not InstagramErrorKind.LoginRequired)
            {
                Ui.Error($"  فشل تعليم ستوري كمشاهَد: {ex.Message}");
                if (ex.Kind == InstagramErrorKind.RateLimited) break;
            }
        }
        Ui.Info($"تم تعليم {ok} من {items.Count} كمشاهَدة.");
    }
}

internal static class Ui
{
    public static void Title(string text)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"═══ {text} ═══");
        Console.ResetColor();
    }

    public static void Info(string text) => Write(ConsoleColor.Gray, text);
    public static void Success(string text) => Write(ConsoleColor.Green, text);
    public static void Warn(string text) => Write(ConsoleColor.Yellow, text);
    public static void Error(string text) => Write(ConsoleColor.Red, "✖ " + text);

    private static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static string Prompt(string label)
    {
        Console.Write($"{label}: ");
        return (Console.ReadLine() ?? "").Trim();
    }

    public static bool Confirm(string question)
    {
        var a = Prompt($"{question} [y/N]").ToLowerInvariant();
        return a is "y" or "yes" or "ن" or "نعم";
    }

    /// <summary>Reads a line without echoing it (falls back to ReadLine when input is redirected).</summary>
    public static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine($" ({sb.Length} حرف)");
        return sb.ToString();
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Warn("لا يوجد رابط لهذا الستوري.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser available (e.g. a headless server): print the link instead.
            Console.WriteLine(url);
        }
    }

    public static string Ago(DateTimeOffset time) => Duration(DateTimeOffset.UtcNow - time) + " مضت";

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) return "منتهي";
        if (span.TotalMinutes < 1) return "أقل من دقيقة";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} دقيقة";
        return $"{(int)span.TotalHours} س {span.Minutes} د";
    }
}
