﻿using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Meziantou.Framework;
using Octokit;
using Octokit.Internal;

namespace IssuesToRss
{
    internal static class Program
    {
        static async Task Main(string[] args)
        {
            var outputDirectory = FullPath.FromPath(args[0]);
            var token = args.Length > 1 ? args[1] : null;

            var repositories = new[]
            {
                "dotnet/aspnetcore",
                "dotnet/roslyn",
                "dotnet/roslyn-analyzers",
                "dotnet/runtime",
            };


            var client = token != null ? new GitHubClient(new ProductHeaderValue("issue-to-rss"), new InMemoryCredentialStore(new Credentials(token)))
                                       : new GitHubClient(new ProductHeaderValue("issue-to-rss"));

            foreach (var repository in repositories)
            {
                // Get issues
                var items = new List<SyndicationItem>();
                var parts = repository.Split('/');
                var repositoryOwner = parts[0];
                var repositoryName = parts[1];

                foreach (var issue in await GetIssuesForRepository(client, repositoryOwner, repositoryName))
                {
                    bool isPullRequest = issue.PullRequest != null;
                    var title = (isPullRequest ? "PR: " : "") + issue.Title;

                    items.Add(new SyndicationItem(title, content: issue.Body, new Uri(issue.HtmlUrl), issue.HtmlUrl, issue.CreatedAt)
                    {
                        Authors =
                        {
                            new SyndicationPerson(issue.User.Email, issue.User.Login, issue.User.HtmlUrl),
                        }
                    });
                }

                // Generate feed
                var feed = new SyndicationFeed
                {
                    Title = new TextSyndicationContent(repository + " issues"),
                    Description = new TextSyndicationContent($"Issues from https://github.com/{repository}, generated by https://github.com/meziantou/IssuesToRss"),
                    Items = items,
                    TimeToLive = TimeSpan.FromHours(1),
                    Authors = { new SyndicationPerson("meziantousite@outlook.com", "Gérald Barré", "https://www.meziantou.net/") },
                };

                var feedPath = FullPath.Combine(outputDirectory, repositoryOwner, repositoryName + ".rss");
                Directory.CreateDirectory(feedPath.Parent);
                using var stream = File.OpenWrite(feedPath);
                using var xmlWriter = XmlWriter.Create(stream);
                var rssFormatter = new Rss20FeedFormatter(feed, serializeExtensionsAsAtom: false);
                rssFormatter.WriteTo(xmlWriter);
                xmlWriter.Flush();
            }

            // index.html
            {
                var indexPath = FullPath.Combine(outputDirectory, "index.html");
                Directory.CreateDirectory(indexPath.Parent);

                var sb = new StringBuilder();
                sb.Append("<ul>");
                foreach (var repository in repositories)
                {
                    sb.Append("<li>");
                    sb.Append($"<a href='{repository}.rss'>{repository}</a>");
                    sb.Append("</li>");
                }
                sb.Append("</ul>");
                sb.Append("<p>Generated by <a href='https://github.com/meziantou/IssuesToRss'>https://github.com/meziantou/IssuesToRss</a></p>");
                File.WriteAllText(indexPath, sb.ToString());
            }
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
    }
}
