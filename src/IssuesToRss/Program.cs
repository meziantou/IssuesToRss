using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.ServiceModel.Syndication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Markdig;
using Meziantou.Framework;
using Meziantou.Framework.Http;

namespace IssuesToRss;

internal static class Configuration
{
    public const string GitHubRepositoryUrl = "https://github.com/meziantou/IssuesToRss/";
    public const string RootUrl = "https://meziantou.github.io/IssuesToRss/";

    public static IReadOnlyCollection<string> Repositories { get; } =
    [
        "dotnet/announcements",
        "dotnet/aspire",
        "dotnet/aspnetcore",
        "dotnet/AspNetCore.Docs",
        "dotnet/csharplang",
        "dotnet/docs",
        "dotnet/docs-desktop",
        "dotnet/efcore",
        "dotnet/EntityFramework.Docs",
        "dotnet/format",
        "dotnet/fsharp",
        "dotnet/interactive",
        "dotnet/machinelearning",
        "dotnet/msbuild",
        "dotnet/orleans",
        "dotnet/roslyn",
        "dotnet/roslyn-analyzers",
        "dotnet/runtime",
        "dotnet/runtimelab",
        "dotnet/sdk",
        "dotnet/SqlClient",
        "dotnet/windowsdesktop",
        "dotnet/winforms",
        "dotnet/wpf",
    ];

    public static IReadOnlySet<string> ExcludedUsers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cxwtool",
        "dependabot",
        "dependabot[bot]",
        "dotnet-bot",
        "dotnet-maestro-bot",
        "dotnet-maestro[bot]",
        "pr-benchmarks[bot]",
    };

    public static IReadOnlySet<string> ExcludedLabels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Type: Dependency Update :arrow_up_small:",
    };
}

internal static class Program
{
    static async Task Main(string[] args)
    {
        var outputDirectory = FullPath.FromPath(args[0]);
        var githubToken = args.Length > 1 ? args[1] : null;
        
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        string ConvertMarkdownToHtml(string markdown)
        {
            try
            {
                return Markdown.ToHtml(markdown ?? "", pipeline);
            }
            catch
            {
                try
                {
                    return Markdown.ToHtml(markdown ?? "");
                }
                catch
                {
                    return markdown;
                }
            }
        }

        var feeds = new List<FeedData>();
        await Configuration.Repositories.ParallelForEachAsync(degreeOfParallelism: 16, async repository =>
        {
            Console.WriteLine("Generating feed for " + repository);

            // Get issues
            var issuesItems = new List<SyndicationItem>(200);
            var prItems = new List<SyndicationItem>(200);
            var parts = repository.Split('/');
            var repositoryOwner = parts[0];
            var repositoryName = parts[1];

            await foreach (var issue in GetIssuesForRepository(repositoryOwner, repositoryName, githubToken).TakeAsync(200))
            {
                if (issue.User?.Login is string login && Configuration.ExcludedUsers.Contains(login))
                    continue;

                if (issue.Labels != null && issue.Labels.Any(label => label.Name != null && Configuration.ExcludedLabels.Contains(label.Name)))
                    continue;

                var isPullRequest = issue.PullRequest != null;
                var title = (isPullRequest ? "PR: " : "Issue: ") + issue.Title + " - @" + issue.User?.Login;

                var item = new SyndicationItem
                {
                    Id = issue.HtmlUrl,
                    Title = new TextSyndicationContent(SanitizeString(title)),
                    Content = new TextSyndicationContent(SanitizeString(ConvertMarkdownToHtml(issue.Body))),
                    Links =
                    {
                        SyndicationLink.CreateAlternateLink(new Uri(issue.HtmlUrl ?? "")),
                    },
                    PublishDate = issue.CreatedAt,
                    LastUpdatedTime = issue.CreatedAt,
                    Authors =
                    {
                        new SyndicationPerson(issue.User?.Email, issue.User?.Login, issue.User?.HtmlUrl),
                    }
                };

                if (isPullRequest)
                {
                    prItems.Add(item);
                }
                else
                {
                    issuesItems.Add(item);
                }
            }

            // Generate feeds
            {
                var issuesFeed = new SyndicationFeed
                {
                    Title = new TextSyndicationContent(repository + " Issues"),
                    Description = new TextSyndicationContent($"Issues from https://github.com/{repository}, generated by {Configuration.GitHubRepositoryUrl}"),
                    Items = issuesItems,
                    TimeToLive = TimeSpan.FromHours(1),
                    Authors = { new SyndicationPerson("dummy@example.com", "Gérald Barré", "https://www.meziantou.net/") },
                };

                lock (feeds)
                {
                    feeds.Add(new FeedData { Feed = issuesFeed, OutputRelativePath = $"{repository}.issues.rss" });
                }
            }

            {
                var prFeed = new SyndicationFeed
                {
                    Title = new TextSyndicationContent(repository + " Pull Requests"),
                    Description = new TextSyndicationContent($"Pull Requests from https://github.com/{repository}, generated by {Configuration.GitHubRepositoryUrl}", TextSyndicationContentKind.Html),
                    Items = prItems,
                    TimeToLive = TimeSpan.FromHours(1),
                    Authors = { new SyndicationPerson("dummy@example.com", "Gérald Barré", "https://www.meziantou.net/") },
                };

                lock (feeds)
                {
                    feeds.Add(new FeedData { Feed = prFeed, OutputRelativePath = $"{repository}.pr.rss" });
                }
            }
        });

        feeds.Sort((a, b) => a.OutputRelativePath.CompareTo(b.OutputRelativePath));

        // Write feeds
        foreach (var feedData in feeds)
        {
            Console.WriteLine($"Writing feed '{feedData.OutputRelativePath}'");
            var feedPath = FullPath.Combine(outputDirectory, feedData.OutputRelativePath);
            Directory.CreateDirectory(feedPath.Parent);
            using var stream = File.OpenWrite(feedPath);
            using var xmlWriter = XmlWriter.Create(stream, new XmlWriterSettings() { Indent = true });
            var rssFormatter = new Rss20FeedFormatter(feedData.Feed, serializeExtensionsAsAtom: false);
            rssFormatter.WriteTo(xmlWriter);
        }

        // index.opml
        {
            Console.WriteLine("Generating opml");
            var indexPath = FullPath.Combine(outputDirectory, "feeds.opml");
            Directory.CreateDirectory(indexPath.Parent);
            var document = new XDocument(
                new XElement("opml", new XAttribute("version", "1.0"),
                    new XElement("head",
                        new XElement("title", "GitHub issues feeds")),
                    new XElement("body", feeds.Select(GetOutline).ToArray())));

            using var stream = File.OpenWrite(indexPath);
            using var xmlWriter = XmlWriter.Create(stream, new XmlWriterSettings() { Indent = true });
            document.WriteTo(xmlWriter);

            static IEnumerable<XElement> GetOutline(FeedData data)
            {
                yield return new XElement("outline",
                    new XAttribute("type", "rss"),
                    new XAttribute("text", data.Feed.Title.Text),
                    new XAttribute("title", data.Feed.Title.Text),
                    new XAttribute("xmlUrl", Configuration.RootUrl + data.OutputRelativePath));
            }
        }

        // index.html
        {
            Console.WriteLine("Generating index");
            var indexPath = FullPath.Combine(outputDirectory, "index.html");
            Directory.CreateDirectory(indexPath.Parent);

            var sb = new StringBuilder();
            foreach (var data in feeds)
            {
                sb.Append("<li>");
                sb.Append($"<a href='{data.OutputRelativePath}'>{HtmlEncoder.Default.Encode(data.Feed.Title.Text)}</a>");
                sb.Append("</li>");
            }

            var template = GetTemplate("templates/index.html");
            template = template
                .Replace("{Feeds}", sb.ToString())
                .Replace("{BUILD_DATE}", HtmlEncoder.Default.Encode(DateTime.UtcNow.ToStringInvariant("O")));

            File.WriteAllText(indexPath, template);
        }
    }

    private static string GetTemplate(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetRequiredManifestResourceStream("IssuesToRss." + name.Replace('/', '.'));
        using var sr = new StreamReader(stream);
        return sr.ReadToEnd();
    }

    private static async IAsyncEnumerable<Issue> GetIssuesForRepository(string owner, string name, string? token)
    {
        var url = $"https://api.github.com/repos/{owner}/{name}/issues?state=all&sort=created&direction=desc&per_page=100&page=1";
        while (url != null)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.UserAgent.Add(new("issuetorss", "1.0.0"));
            if (token != null)
            {
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await SharedHttpClient.Instance.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();
            var issues = await response.Content.ReadFromJsonAsync<Issue[]>();
            foreach (var issue in issues!)
                yield return issue;

            url = httpRequest.Headers.ParseLinkHeaders().FirstOrDefault(link => link.Rel == "next")?.Url;
        }
    }

    private static string SanitizeString(string str)
    {
        if (str is null)
            return "";

        var sb = new StringBuilder(str.Length);
        Span<char> chars = stackalloc char[2];
        foreach (var rune in str.EnumerateRunes())
        {
            if (!rune.TryEncodeToUtf16(chars, out var written))
                continue;

            if (written == 1)
            {
                if (!XmlConvert.IsXmlChar(chars[0]))
                    continue;
            }
            else if (written == 2)
            {
                if (!XmlConvert.IsXmlSurrogatePair(chars[0], chars[1]))
                    continue;
            }
            else
            {
                throw new Exception("written = " + written);
            }

            sb.Append(chars[..written]);
        }

        return sb.ToString();
    }

    private sealed class FeedData
    {
        public required string OutputRelativePath { get; set; }
        public required SyndicationFeed Feed { get; set; }
    }

    private sealed class Issue
    {
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("labels")]
        public Label[]? Labels { get; set; }

        [JsonPropertyName("user")]
        public User? User { get; set; }

        [JsonPropertyName("pull_request")]
        public object? PullRequest { get; set; }
    }

    private sealed class User
    {
        [JsonPropertyName("login")]
        public string? Login { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    private sealed class Label
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
