// TikTok username availability checker.
// Checks candidate usernames against the public TikTok profile page and
// reports which ones appear to be free.
//
//   dotnet run -- yousef yousef.dev yousef_01
//   dotnet run -- -f names.txt
//   dotnet run -- --generate 3 --count 30 --prefix yo

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TikTokUsernameChecker;

enum Status { Available, Taken, Invalid, Unknown }

sealed record CheckResult(Status Status, string Detail);

sealed class Options
{
    public List<string> Names { get; } = new();
    public string? File { get; set; }
    public int GenerateLength { get; set; }
    public int Count { get; set; } = 20;
    public string Chars { get; set; } = "abcdefghijklmnopqrstuvwxyz0123456789";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    public double DelaySeconds { get; set; } = 2.0;
    public string Output { get; set; } = "available.txt";
}

sealed class TikTokChecker : IDisposable
{
    const string ProfileUrl = "https://www.tiktok.com/@{0}?lang=en";
    const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    // TikTok rules: 2-24 chars, letters/digits/underscore/period, no trailing period.
    static readonly Regex ValidName = new(@"^[A-Za-z0-9_.]{2,24}$", RegexOptions.Compiled);
    static readonly Regex DataScript = new(
        @"<script[^>]+id=""__UNIVERSAL_DATA_FOR_REHYDRATION__""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    readonly HttpClient _http;

    public TikTokChecker()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public static bool IsValid(string name) => ValidName.IsMatch(name) && !name.EndsWith('.');

    public async Task<CheckResult> CheckAsync(string name, int retries = 3)
    {
        if (!IsValid(name))
            return new(Status.Invalid, "does not match TikTok username rules");

        string detail = "";
        for (int attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(string.Format(ProfileUrl, name));
                var html = await resp.Content.ReadAsStringAsync();
                var code = (int)resp.StatusCode;

                if (code == 404)
                    return new(Status.Available, "profile not found (404)");
                if (code == 429)
                    detail = "rate limited (429)";
                else if (ParsePage(html) is { } parsed)
                    return parsed;
                else if (html.Contains("Couldn't find this account"))
                    return new(Status.Available, "no account with this name");
                else
                    detail = $"HTTP {code}, unexpected page (captcha/block?)";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                detail = e.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1))); // back off before retrying
        }
        return new(Status.Unknown, detail);
    }

    static CheckResult? ParsePage(string html)
    {
        var match = DataScript.Match(html);
        if (!match.Success)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("__DEFAULT_SCOPE__", out var scope) ||
                !scope.TryGetProperty("webapp.user-detail", out var detail))
                return null;

            int? statusCode = detail.TryGetProperty("statusCode", out var sc) &&
                              sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : null;
            bool hasUser = detail.TryGetProperty("userInfo", out var info) &&
                           info.TryGetProperty("user", out var user) &&
                           user.ValueKind == JsonValueKind.Object &&
                           user.EnumerateObject().Any();

            return statusCode switch
            {
                0 => new(Status.Taken, "account exists"),
                10222 => new(Status.Taken, "private account"),
                10221 => new(Status.Available, "no account with this name"),
                _ when hasUser => new(Status.Taken, "account exists"),
                _ => new(Status.Unknown, $"statusCode={statusCode}"),
            };
        }
        catch (JsonException e)
        {
            return new(Status.Unknown, $"could not parse page: {e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}

static class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Options opts;
        try
        {
            opts = ParseArgs(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            PrintUsage();
            return 2;
        }

        var names = CollectNames(opts);
        if (names.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var checker = new TikTokChecker();
        var available = new List<string>();

        for (int i = 0; i < names.Count && !cts.IsCancellationRequested; i++)
        {
            var name = names[i];
            var result = await checker.CheckAsync(name);
            PrintResult(i + 1, names.Count, name, result);

            if (result.Status == Status.Available)
            {
                available.Add(name);
                await File.AppendAllTextAsync(opts.Output, name + Environment.NewLine);
            }

            if (i < names.Count - 1 && result.Status != Status.Invalid)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(opts.DelaySeconds), cts.Token); }
                catch (TaskCanceledException) { }
            }
        }

        if (cts.IsCancellationRequested)
            Console.WriteLine("\nStopped.");

        Console.WriteLine();
        Console.WriteLine($"Available ({available.Count}): " +
                          (available.Count > 0 ? string.Join(", ", available.Select(n => "@" + n)) : "-"));
        if (available.Count > 0)
        {
            Console.WriteLine($"Saved to {opts.Output}. Confirm in the TikTok app before relying on it:");
            Console.WriteLine("some names are banned/reserved and still can't be claimed.");
        }
        return 0;
    }

    static List<string> CollectNames(Options opts)
    {
        var raw = new List<string>(opts.Names);
        if (opts.File != null)
            raw.AddRange(File.ReadAllLines(opts.File));
        if (opts.GenerateLength > 0)
            raw.AddRange(Generate(opts));

        // Normalize, drop blanks, dedupe while keeping order.
        return raw.Select(n => n.Trim().TrimStart('@').ToLowerInvariant())
                  .Where(n => n.Length > 0)
                  .Distinct()
                  .ToList();
    }

    static IEnumerable<string> Generate(Options opts)
    {
        var seen = new HashSet<string>();
        int attempts = 0;
        while (seen.Count < opts.Count && attempts++ < opts.Count * 100)
        {
            var body = new string(Enumerable.Range(0, opts.GenerateLength)
                .Select(_ => opts.Chars[Random.Shared.Next(opts.Chars.Length)])
                .ToArray());
            var name = opts.Prefix + body + opts.Suffix;
            if (TikTokChecker.IsValid(name))
                seen.Add(name);
        }
        return seen.OrderBy(n => n);
    }

    static void PrintResult(int index, int total, string name, CheckResult result)
    {
        var color = result.Status switch
        {
            Status.Available => ConsoleColor.Green,
            Status.Taken => ConsoleColor.Red,
            Status.Invalid => ConsoleColor.Yellow,
            _ => ConsoleColor.Cyan,
        };
        Console.Write($"[{index}/{total}] ");
        Console.ForegroundColor = color;
        Console.Write(result.Status.ToString().ToUpperInvariant().PadRight(10));
        Console.ResetColor();
        Console.WriteLine($"@{name}  ({result.Detail})");
    }

    static Options ParseArgs(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i]
                : throw new ArgumentException($"missing value for {args[i]}");

            switch (args[i])
            {
                case "-h" or "--help": PrintUsage(); Environment.Exit(0); break;
                case "-f" or "--file": opts.File = Next(); break;
                case "--generate": opts.GenerateLength = int.Parse(Next()); break;
                case "--count": opts.Count = int.Parse(Next()); break;
                case "--chars": opts.Chars = Next(); break;
                case "--prefix": opts.Prefix = Next(); break;
                case "--suffix": opts.Suffix = Next(); break;
                case "-d" or "--delay":
                    opts.DelaySeconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "-o" or "--output": opts.Output = Next(); break;
                default:
                    if (args[i].StartsWith("--"))
                        throw new ArgumentException($"unknown option {args[i]}");
                    opts.Names.Add(args[i]);
                    break;
            }
        }
        if (opts.Chars.Length == 0)
            throw new ArgumentException("--chars cannot be empty");
        return opts;
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: check-tiktok [names...] [options]

              -f, --file <path>     text file with one username per line
              --generate <len>      generate random candidates with <len> random characters
              --count <n>           how many to generate (default 20)
              --chars <set>         characters to generate from (default a-z0-9)
              --prefix <text>       fixed text before the generated part
              --suffix <text>       fixed text after the generated part
              -d, --delay <sec>     seconds between requests (default 2)
              -o, --output <path>   file to save available usernames (default available.txt)
            """);
    }
}
