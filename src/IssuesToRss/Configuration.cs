using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace IssuesToRss;

internal static class Configuration
{
    public const string GitHubRepositoryUrl = "https://github.com/meziantou/IssuesToRss/";
    public const string RootUrl = "https://meziantou.github.io/IssuesToRss/";

    public static IReadOnlyCollection<string> Repositories { get; } =
    [
        "dotnet/announcements",
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
        "microsoft/aspire",
    ];

    public static IReadOnlySet<string> ExcludedUsers { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cxwtool",
        "dependabot",
        "dependabot[bot]",
        "dotnet-bot",
        "dotnet-bot[bot]",
        "dotnet-policy-service[bot]",
        "dotnet-maestro-bot",
        "dotnet-maestro[bot]",
        "pr-benchmarks[bot]",
    };

    public static IReadOnlySet<string> ExcludedLabels { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Type: Dependency Update :arrow_up_small:",
        "test-failure",
    };

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ExcludedTitlePrefixes { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet/aspire"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[Deployment E2E] Nightly test failure",
        },
    };

    public static IReadOnlyDictionary<string, IReadOnlyCollection<Regex>> ExcludedTitleRegexes { get; } = new Dictionary<string, IReadOnlyCollection<Regex>>(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft/aspire"] =
        [
            new Regex(@"^\[.*-burndown\] Daily Burndown Report", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        ],
    };
}
