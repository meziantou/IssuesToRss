﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceModel.Syndication;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Meziantou.Framework;
using Octokit;
using Octokit.Internal;

namespace IssuesToRss
{
    internal static class Configuration
    {
        public const string GitHubRepositoryUrl = "https://github.com/meziantou/IssuesToRss/";
        public const string RootUrl = "https://meziantou.github.io/IssuesToRss/";

        public static IReadOnlyCollection<string> Repositories { get; } = new[]
        {
            "dotnet/aspnetcore",
            "dotnet/csharplang",
            "dotnet/efcore",
            "dotnet/format",
            "dotnet/machinelearning",
            "dotnet/orleans",
            "dotnet/roslyn",
            "dotnet/roslyn-analyzers",
            "dotnet/runtime",
            "dotnet/SqlClient",
            "dotnet/windowsdesktop",
            "dotnet/winforms",
            "dotnet/wpf",
        };

        public static IReadOnlySet<string> ExcludedUsers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dotnet-bot",
            "dotnet-maestro-bot",
            "dotnet-maestro[bot]",
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
            var client = githubToken != null ? new GitHubClient(new ProductHeaderValue("issue-to-rss"), new InMemoryCredentialStore(new Credentials(githubToken)))
                                             : new GitHubClient(new ProductHeaderValue("issue-to-rss"));

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

                foreach (var issue in await GetIssuesForRepository(client, repositoryOwner, repositoryName))
                {
                    if (Configuration.ExcludedUsers.Contains(issue.User.Login))
                        continue;

                    if (issue.Labels.Any(label => Configuration.ExcludedLabels.Contains(label.Name)))
                        continue;

                    var isPullRequest = issue.PullRequest != null;
                    var title = (isPullRequest ? "PR: " : "") + issue.Title;

                    var item = new SyndicationItem(title, content: issue.Body, new Uri(issue.HtmlUrl), issue.HtmlUrl, issue.CreatedAt)
                    {
                        Authors =
                        {
                            new SyndicationPerson(issue.User.Email, issue.User.Login, issue.User.HtmlUrl),
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
                        Authors = { new SyndicationPerson("meziantousite@outlook.com", "Gérald Barré", "https://www.meziantou.net/") },
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
                        Description = new TextSyndicationContent($"Pull Requests from https://github.com/{repository}, generated by {Configuration.GitHubRepositoryUrl}"),
                        Items = prItems,
                        TimeToLive = TimeSpan.FromHours(1),
                        Authors = { new SyndicationPerson("meziantousite@outlook.com", "Gérald Barré", "https://www.meziantou.net/") },
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
                        new XAttribute("text", data.Feed.Title),
                        new XAttribute("title", data.Feed.Title),
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
                template = template.Replace("{Feeds}", sb.ToString());

                File.WriteAllText(indexPath, template);
            }
        }

        private static string GetTemplate(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IssuesToRss." + name.Replace('/', '.'));
            using var sr = new StreamReader(stream);
            return sr.ReadToEnd();
        }

        private static async Task<IReadOnlyCollection<Issue>> GetIssuesForRepository(GitHubClient client, string owner, string name)
        {
            var request = new RepositoryIssueRequest
            {
                State = ItemStateFilter.All,
                SortProperty = IssueSort.Created,
                SortDirection = SortDirection.Descending,
            };

            var options = new ApiOptions
            {
                StartPage = 0,
                PageSize = 100,
                PageCount = 2,
            };
            return await client.Issue.GetAllForRepository(owner, name, request, options);
        }

        private sealed class FeedData
        {
            public string OutputRelativePath { get; set; }
            public SyndicationFeed Feed { get; set; }
        }
    }
}
