// commit-collector-azdo - Single-file version
// Collect and filter commits from Azure DevOps commit ranges
// This is a standalone .cs file that can be run without a .csproj
// Usage: dotnet run Program.cs -- <arguments>

#:package Azure.Identity@1.13.1
#:package System.CommandLine@2.0.0-beta4.22272.1

using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Core;
using Azure.Identity;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.CommandLine;
using System.Threading;
using System.Text;

// Define command-line options
RootCommand rootCommand = new("commit-collector-azdo - Collect and filter commits from Azure DevOps commit ranges");

Option<string> optionFromCommit = new(["-from-commit", "-fc"], "The starting commit ID (SHA) to collect commits from.")
{
    IsRequired = true
};
Option<string> optionOrg = new(["-org", "-o"], "The Azure DevOps organization name.")
{
    IsRequired = true
};
Option<string> optionProject = new(["-project", "-p"], "The Azure DevOps project name.")
{
    IsRequired = true
};
Option<string> optionRepo = new(["-repo", "-r"], "The repository name or ID.")
{
    IsRequired = true
};
Option<string?> optionGitHubOrg = new(["-github-org", "-go"], "The GitHub organization name (optional, for fetching PR details).");
Option<string?> optionGitHubRepo = new(["-github-repo", "-gr"], "The GitHub repository name (optional, for fetching PR details).");
Option<string> optionBranch = new(["--branch", "-b"], "The branch name to filter commits.")
{
    IsRequired = true
};
Option<bool> optionDebug = new(["--debug", "-d"], () => false, "Break execution at the beginning to attach a debugger.");

rootCommand.Add(optionFromCommit);
rootCommand.Add(optionOrg);
rootCommand.Add(optionProject);
rootCommand.Add(optionRepo);
rootCommand.Add(optionGitHubOrg);
rootCommand.Add(optionGitHubRepo);
rootCommand.Add(optionBranch);
rootCommand.Add(optionDebug);

rootCommand.SetHandler(async (context) =>
{
    var stopwatch = Stopwatch.StartNew();
    
    var fromCommit = context.ParseResult.GetValueForOption(optionFromCommit);
    var org = context.ParseResult.GetValueForOption(optionOrg);
    var project = context.ParseResult.GetValueForOption(optionProject);
    var repo = context.ParseResult.GetValueForOption(optionRepo);
    var githubOrg = context.ParseResult.GetValueForOption(optionGitHubOrg);
    var githubRepo = context.ParseResult.GetValueForOption(optionGitHubRepo);
    var branch = context.ParseResult.GetValueForOption(optionBranch);
    var debug = context.ParseResult.GetValueForOption(optionDebug);
    
    ConsoleLog.WriteInfo($"Azure DevOps Commit Collector");
    ConsoleLog.WriteInfo($"========================================");
    ConsoleLog.WriteInfo($"Organization: {org}");
    ConsoleLog.WriteInfo($"Project: {project}");
    ConsoleLog.WriteInfo($"Repository: {repo}");
    ConsoleLog.WriteInfo($"From Commit: {fromCommit}");
    ConsoleLog.WriteInfo($"Branch: {branch}");
    if (!string.IsNullOrWhiteSpace(githubOrg) && !string.IsNullOrWhiteSpace(githubRepo))
    {
        ConsoleLog.WriteInfo($"GitHub: {githubOrg}/{githubRepo}");
    }
    
    if (debug)
    {
        Debugger.Launch();
        while (!Debugger.IsAttached)
        {
            ConsoleLog.WriteWarning($"Attach to {Environment.ProcessId}...");
            Thread.Sleep(1000);
        }
        ConsoleLog.WriteSuccess("Attached!");
        Debugger.Break();
    }

    CommitCollectorAzDO collector = await CommitCollectorAzDO.CreateAsync(org!, project!, repo!, githubOrg, githubRepo);
    await collector.RunAsync(fromCommit!, branch!);
    
    stopwatch.Stop();
    ConsoleLog.WriteSuccess($"Total execution time: {stopwatch.Elapsed.TotalSeconds:F2} seconds ({stopwatch.Elapsed:mm\\:ss\\.fff})");
});

await rootCommand.InvokeAsync(args);

// JSON Source Generator Context
[JsonSerializable(typeof(CommitChanges))]
[JsonSerializable(typeof(AzureDevOpsList<Commit>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
partial class SourceGenerationContext : JsonSerializerContext
{
}

// Core class and helper types
partial class CommitCollectorAzDO
{
    private const string UserNameAzurePipelinesBot = "azure-pipelines[bot]";
    private const string UserNameMaestroBot = "dotnet-maestro[bot]";
    private readonly string[] InfraExtensions = [
        "CMakeLists.txt", ".cmake", ".config", ".csproj", ".editorconfig", 
        ".gitignore", ".ilproj", ".inc", ".json", ".md", ".pp", ".proj", 
        ".props", ".ps1", ".ruleset", ".S", ".sln", ".targets", ".txt", 
        ".xml", ".yml", ".yaml"
    ];
    private readonly string[] ForbiddenStrings = [
        "Update dependencies from ",
        "Merge branch "
    ];
    private readonly string[] ForbiddenPatterns = [
        @"Merge pull request (dotnet)?\#\d+ from "
    ];
    private readonly string[] StringsToTrim = [
        "[release/8.0] ",
        "[release/9.0] ",
        "[release/10.0] ",
        "[release/8.0-staging] ",
        "[release/9.0-staging] ",
        "[release/10.0-staging] ",
    ];
    private readonly string[] StringPatternsToTrim = [
        @"[ ]*\(\#\d+\)",
        @"\[\d+\.0\] "
    ];
    private static readonly char[] NewLineChar = ['\n'];

    private readonly List<string> _errors;
    private readonly string _repository;
    private readonly GitHubCommitFetcher? _githubFetcher;

    public AzureDevOpsCommunicator Communicator { get; }

    private CommitCollectorAzDO(AzureDevOpsCommunicator communicator, string organization, string project, string repository, string? githubOrg, string? githubRepo, GitHubCommitFetcher? githubFetcher)
    {
        Communicator = communicator;
        _repository = repository;
        _githubFetcher = githubFetcher;
        _errors = new List<string>();
    }

    public static async Task<CommitCollectorAzDO> CreateAsync(string organization, string project, string repository, string? githubOrg = null, string? githubRepo = null)
    {
        AzureDevOpsCommunicator communicator = new AzureDevOpsCommunicator(organization, project);
        
        // Initialize GitHub fetcher if GitHub org/repo provided
        GitHubCommitFetcher? githubFetcher = null;
        if (!string.IsNullOrWhiteSpace(githubOrg) && !string.IsNullOrWhiteSpace(githubRepo))
        {
            ConsoleLog.WriteInfo("GitHub repository specified. Will fetch PR and approver information from GitHub.");
            githubFetcher = await GitHubCommitFetcher.CreateAsync(githubOrg, githubRepo);
        }
        
        return new CommitCollectorAzDO(communicator, organization, project, repository, githubOrg, githubRepo, githubFetcher);
    }

    public async Task RunAsync(string fromCommitId, string branchName)
    {
        ConsoleLog.WriteInfo($"Fetching commits after '{fromCommitId[..8]}...' on branch '{branchName}'...");
        
        AzureDevOpsList<Commit> commits = await Communicator.GetCommitsAfterCommit(_repository, fromCommitId, branchName);
        
        ConsoleLog.WriteSuccess($"Found {commits.Count} commits");

        List<(Commit, CommitChanges)> included = new();
        List<(Commit, string)> skipped = new();

        foreach (Commit commit in commits.Value)
        {
            await ProcessCommitAsync(commit, included, skipped);
        }

        await PrintIncludedTableAsync(included, fromCommitId);
        PrintSkippedTable(skipped);
        PrintErrors();
    }

    public async Task ProcessCommitAsync(Commit commit, List<(Commit, CommitChanges)> included, List<(Commit, string)> skipped)
    {
        // Get commit changes
        string api = $"git/repositories/{_repository}/commits/{commit.CommitId}/changes";
        string response = await Communicator.CallAsync(api, []);
        CommitChanges changes = JsonSerializer.Deserialize(response, SourceGenerationContext.Default.CommitChanges)
            ?? throw new Exception($"Could not deserialize commit changes for {commit.CommitId}");

        if (IsSkippable(commit, changes, out string? reason))
        {
            skipped.Add((commit, reason));
        }
        else
        {
            included.Add((commit, changes));
        }
    }

    private async Task<GitHubCommitInfo?> GetGitHubInfoAsync(string commitMessage)
    {
        if (_githubFetcher == null)
            return null;

        // Extract PR number from commit message (e.g., "Title (#12345)" or "Merge pull request #12345")
        Match match = Regex.Match(commitMessage, @"\(#(?<prNumber>\d+)\)|#(?<prNumber>\d+)");
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["prNumber"].Value, out int prNumber))
            return null;

        try
        {
            return await _githubFetcher.GetPullRequestInfoAsync(prNumber);
        }
        catch (Exception ex)
        {
            _errors.Add($"Failed to fetch GitHub PR #{prNumber}: {ex.Message}");
            return null;
        }
    }

    private async Task PrintIncludedTableAsync(List<(Commit, CommitChanges)> included, string fromCommitId)
    {
        var table = new MarkdownTableBuilder().WithHeader("Commit", "Author / Approvers", "Comments", "Validation status");
        foreach ((Commit commit, CommitChanges changes) in included)
        {
            string commitUrl = $"https://dev.azure.com/{Communicator.Organization}/{Communicator.Project}/_git/{_repository}/commit/{commit.CommitId}";
            ReadOnlySpan<char> firstLine = GetFirstLine(commit.Comment);
            string cleanedLine = RemoveUndesiredTexts(firstLine);
            
            // Try to get GitHub PR info
            GitHubCommitInfo? ghInfo = await GetGitHubInfoAsync(commit.Comment);
            string url = ghInfo?.PrUrl ?? commitUrl;
            string authorAndApprovers = ghInfo != null 
                ? $"{ghInfo.Author} / {string.Join(", ", ghInfo.Approvers)}"
                : commit.Author.Name;
            
            table = table.WithRow($"[{cleanedLine}]({url})",
                          authorAndApprovers,
                          string.Empty /* Comments */,
                          string.Empty /* Validation status */);
        }

        ConsoleLog.WriteWarning("-----");
        Console.WriteLine();
        ConsoleLog.WriteSuccess($"Commits after {fromCommitId[..8]}...:");
        ConsoleLog.WriteSuccess(table.ToString());
        Console.WriteLine();
        ConsoleLog.WriteWarning("-----");
        Console.WriteLine();
    }

    private void PrintSkippedTable(List<(Commit, string)> skipped)
    {
        var table = new MarkdownTableBuilder().WithHeader("Reason", "Title");
        foreach ((Commit commit, string reason) in skipped)
        {
            ReadOnlySpan<char> firstLine = GetFirstLine(commit.Comment);
            string cleanedLine = RemoveUndesiredTexts(firstLine);
            table = table.WithRow(reason, $"{cleanedLine}");
        }

        ConsoleLog.WriteWarning("Commits that were skipped:");
        ConsoleLog.WriteWarning(table.ToString());
        Console.WriteLine();
        ConsoleLog.WriteWarning("-----");
        Console.WriteLine();
    }

    private void PrintErrors()
    {
        if (_errors.Any())
        {
            ConsoleLog.WriteError("Processing errors:");
            foreach (string error in _errors)
            {
                ConsoleLog.WriteError(error);
            }
        }
    }

    private bool IsSkippable(Commit commit, CommitChanges changes, [NotNullWhen(returnValue: true)] out string? reason)
    {
        reason = null;

        ReadOnlySpan<char> firstMessageLine = GetFirstLine(commit.Comment);

        // If the author is the Maestro bot, then the commit is skippable
        if (commit.Author.Name.Contains(UserNameMaestroBot, StringComparison.OrdinalIgnoreCase) ||
            commit.Author.Email.Contains(UserNameMaestroBot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "author: maestrobot";
            return true;
        }

        // If the author is the Azure Pipelines bot, then the commit is skippable
        if (commit.Author.Name.Contains(UserNameAzurePipelinesBot, StringComparison.OrdinalIgnoreCase) ||
            commit.Author.Email.Contains(UserNameAzurePipelinesBot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "author: azure-pipelines[bot]";
            return true;
        }

        foreach (string text in ForbiddenStrings)
        {
            // If the first line of the commit message contains any of the forbidden strings, then the commit is skippable
            if (firstMessageLine.Contains(text, StringComparison.InvariantCulture))
            {
                reason = $"Skip title text: {text}";
                return true;
            }
        }

        foreach (string pattern in ForbiddenPatterns)
        {
            // If the first line of the commit message matches any of the forbidden patterns, then the commit is skippable
            if (Regex.IsMatch(firstMessageLine, pattern))
            {
                reason = $"Skip title pattern: {pattern[..19]}";
                return true;
            }
        }

        // If all files in the commit are infra files, then the commit is skippable
        if (changes.Changes != null && changes.Changes.Count > 0 &&
            changes.Changes.All(change => change.Item != null && 
                InfraExtensions.Any(ext => change.Item.Path?.EndsWith(ext, StringComparison.InvariantCultureIgnoreCase) ?? false)))
        {
            reason = "All infra files";
            return true;
        }

        // If all files in the commit are test files, then the commit is skippable
        if (changes.Changes != null && changes.Changes.Count > 0 &&
            changes.Changes.All(change => change.Item != null && 
                (change.Item.Path?.Contains("test", StringComparison.InvariantCultureIgnoreCase) ?? false)))
        {
            reason = "All test files";
            return true;
        }

        return false;
    }

    private string RemoveUndesiredTexts(ReadOnlySpan<char> message)
    {
        string result = message.ToString();

        foreach (string s in StringsToTrim)
        {
            result = result.Replace(s, string.Empty, StringComparison.InvariantCultureIgnoreCase);
        }

        foreach (string pattern in StringPatternsToTrim)
        {
            result = Regex.Replace(result, pattern, string.Empty, RegexOptions.IgnoreCase);
        }

        return result;
    }

    private ReadOnlySpan<char> GetFirstLine(string txt)
    {
        int index = txt.IndexOfAny(NewLineChar);
        if (index == -1)
        {
            index = txt.Length;
        }

        return txt.AsSpan(0, index);
    }
}

// Helper classes for Azure DevOps commit changes
class CommitChanges
{
    public List<CommitChange>? Changes { get; set; }
    public CommitChangeCounts? ChangeCounts { get; set; }
}

class CommitChangeCounts
{
    public int Edit { get; set; }
    public int Add { get; set; }
    public int Delete { get; set; }
}

class CommitChange
{
    public CommitItem? Item { get; set; }
    public string? ChangeType { get; set; }
}

class CommitItem
{
    public string? ObjectId { get; set; }
    public string? Path { get; set; }
    public bool IsFolder { get; set; }
}

// GitHub integration classes
class GitHubCommitInfo
{
    public string PrUrl { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public List<string> Approvers { get; set; } = new();
}

class GitHubTokenProvider
{
    public static async Task<string> GetTokenAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new Exception("Failed to start 'gh' process");
            }

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new Exception($"'gh auth token' failed: {error}. Make sure you've run 'gh auth login' first.");
            }

            string token = output.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new Exception("'gh auth token' returned empty token. Run 'gh auth login' first.");
            }

            ConsoleLog.WriteSuccess("Successfully obtained GitHub token from 'gh' CLI");
            return token;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to get GitHub token from 'gh' CLI: {ex.Message}. Install 'gh' and run 'gh auth login' first.", ex);
        }
    }
}

class GitHubCommitFetcher : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _org;
    private readonly string _repo;
    private readonly string _token;

    private GitHubCommitFetcher(string org, string repo, string token)
    {
        _org = org;
        _repo = repo;
        _token = token;
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CommitCollectorAzDO", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static async Task<GitHubCommitFetcher> CreateAsync(string org, string repo)
    {
        string token = await GitHubTokenProvider.GetTokenAsync();
        return new GitHubCommitFetcher(org, repo, token);
    }

    public async Task<GitHubCommitInfo> GetPullRequestInfoAsync(int prNumber)
    {
        string url = $"https://api.github.com/repos/{_org}/{_repo}/pulls/{prNumber}";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var info = new GitHubCommitInfo
        {
            PrUrl = root.GetProperty("html_url").GetString() ?? string.Empty,
            Author = root.GetProperty("user").GetProperty("login").GetString() ?? "unknown"
        };

        // Get approvers from reviews
        string reviewsUrl = $"https://api.github.com/repos/{_org}/{_repo}/pulls/{prNumber}/reviews";
        var reviewsResponse = await _httpClient.GetAsync(reviewsUrl);
        reviewsResponse.EnsureSuccessStatusCode();
        
        string reviewsJson = await reviewsResponse.Content.ReadAsStringAsync();
        using var reviewsDoc = JsonDocument.Parse(reviewsJson);
        
        var approvers = new HashSet<string>();
        foreach (var review in reviewsDoc.RootElement.EnumerateArray())
        {
            if (review.TryGetProperty("state", out var state) && 
                state.GetString() == "APPROVED" &&
                review.TryGetProperty("user", out var user) &&
                user.TryGetProperty("login", out var login))
            {
                string loginStr = login.GetString() ?? "";
                if (!string.IsNullOrEmpty(loginStr) && loginStr != info.Author)
                {
                    approvers.Add(loginStr);
                }
            }
        }

        info.Approvers = approvers.ToList();
        return info;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

// ========================================
// Inlined Dependencies
// ========================================

// ConsoleLog (from Shared/ConsoleLog.cs)
static class ConsoleLog
{
    enum LogType
    {
        Error = 1,
        Warning = 2,
        Information = 4,
        Success = 8,
        Failure = 16,
    }

    public static void WriteInfo(string message) => WriteInternal(message, LogType.Information);
    public static void WriteWarning(string message) => WriteInternal(message, LogType.Warning);
    public static void WriteError(string message) => WriteInternal(message, LogType.Error);
    public static void WriteFailure(string message) => WriteInternal(message, LogType.Failure);
    public static void WriteSuccess(string message) => WriteInternal(message, LogType.Success);

    static void WriteInternal(string message, LogType type)
    {
        Validate(message, type);
        ConsoleWrite(message, type);
    }

    static void ConsoleWrite(string message, LogType type)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        ConsoleColor newColor = type switch
        {
            LogType.Information => ConsoleColor.White,
            LogType.Warning => ConsoleColor.Yellow,
            LogType.Error => ConsoleColor.Red,
            LogType.Success => ConsoleColor.Green,
            LogType.Failure => ConsoleColor.DarkRed,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        Console.ForegroundColor = newColor;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    static void Validate(string message, LogType type)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}

// MarkdownTableBuilder (from MarkdownTable/MarkdownTableBuilder.cs)
class MarkdownTableBuilder
{
    private string[] header = Array.Empty<string>();
    private readonly List<object[]> rows = new();
    private readonly char verticalChar;
    private readonly char horizontalChar;
    private readonly char outerBorderChar;
    private readonly int padding;
    private readonly StringBuilder rowBuilder;

    public MarkdownTableBuilder()
    {
        rowBuilder = new StringBuilder();
        horizontalChar = '-';
        outerBorderChar = ' ';
        verticalChar = '|';
        padding = 1;
    }

    public MarkdownTableBuilder WithHeader(params string[] header)
    {
        this.header = header;
        return this;
    }

    public MarkdownTableBuilder WithRow(params object[] row)
    {
        rows.Add(row);
        return this;
    }

    public override string ToString()
    {
        var output = new StringBuilder();
        var maxCols = MaxColumns();

        if (header.Length > 0)
        {
            output.AppendLine(Row(header, maxCols));
        }

        output.AppendLine(HorizontalLine());

        rows.ForEach(row => { output.AppendLine(Row(row, maxCols)); });

        return output.ToString();
    }

    int ColumnWidth(int index)
    {
        var width = 1;

        if (header != null && index < header.Length)
        {
            width = header[index].Length;
        }

        return Column(index).Length == 0
            ? 1
            : Math.Max(width, Column(index).Max(r => r != null ? r.Length : 0));
    }

    int[] SizeRow()
    {
        var row = new List<int>();
        var maxCols = MaxColumns();
        for (var i = 0; i < maxCols; i++)
        {
            row.Add(ColumnWidth(i));
        }

        return row.ToArray();
    }

    int MaxColumns()
    {
        var result = 0;
        if (header != null)
        {
            result = header.Length;
        }

        rows.ForEach(row => { result = Math.Max(row.Length, result); });
        return result;
    }

    string[] Column(int index)
    {
        var column = new List<string>();
        rows.ForEach(row => { column.Add(index < row.Length ? row[index].ToString() ?? string.Empty : string.Empty); });
        return column.ToArray();
    }

    static string Fill(int size, char fillChar = ' ')
    {
        return new string(fillChar, Math.Max(size, 0));
    }

    string HorizontalLine()
    {
        var format = Fill(1, outerBorderChar) + "{0}" + Fill(1, outerBorderChar);
        var content = SizeRow()
            .Select(col => Fill(col + 2 * padding, horizontalChar))
            .Aggregate((a, b) => a + Fill(1, verticalChar) + b);
        return string.Format(format, content);
    }

    string Row(object[] row, int maxCols)
    {
        rowBuilder.Length = 0;
        rowBuilder.Append(outerBorderChar);

        for (var i = 0; i < row.Length; i++)
        {
            var maxColWidth = ColumnWidth(i);
            var format = "{0,-" + maxColWidth + "}";

            rowBuilder.Append(Fill(padding));
            rowBuilder.Append(string.Format(format, row[i]));
            rowBuilder.Append(Fill(padding));
            rowBuilder.Append(i == maxCols - 1 ? outerBorderChar : verticalChar);
        }

        var j = row.Length - 1;
        while (j++ < maxCols - 1)
        {
            var maxColWidth = ColumnWidth(j);
            rowBuilder.Append(Fill(maxColWidth + 2 * padding));
            rowBuilder.Append(j == maxCols - 1 ? outerBorderChar : verticalChar);
        }

        return rowBuilder.ToString();
    }
}

// AzureDevOps classes (from Connectors/AzureDevOps/)
class AzureDevOpsList<T>
{
    public int Count { get; set; }
    public List<T> Value { get; set; } = new List<T>();
}

class Commit
{
    public string CommitId { get; set; } = string.Empty;
    public CommitPerson Author { get; set; } = new();
    public CommitPerson Committer { get; set; } = new();
    public string Comment { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
}

class CommitPerson
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
}

class CommitOptions
{
    public string Repo { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public required string FromCommitId { get; set; }
    public Dictionary<string, string> Arguments { get; set; } = [];
}

class AzureDevOpsCommunicator : IDisposable
{
    private readonly TokenCredential _credential;
    private readonly string _organization;
    private readonly string _project;
    private readonly HttpClient _httpClient;
    
    public string Organization => _organization;
    public string Project => _project;
    
    string AzureDevOpsUrl => "https://dev.azure.com";

    public AzureDevOpsCommunicator(string organization, string project)
    {
        _organization = organization;
        _project = project;
        _credential = new DefaultAzureCredential();

        MediaTypeWithQualityHeaderValue jsonMediaType = new("application/json");
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(jsonMediaType);
    }

    public void Dispose() => _httpClient.Dispose();

    public Task<string> CallAsync(string api, Dictionary<string, string> arguments)
    {
        string query = GetQueryUrl(api, arguments);
        return CallAsync(query);
    }

    async Task<string> CallAsync(string query)
    {
        var tokenRequestContext = new TokenRequestContext(new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" });
        var token = await _credential.GetTokenAsync(tokenRequestContext, default);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using HttpResponseMessage response = await _httpClient.GetAsync(query);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    string GetQueryUrl(string api, Dictionary<string, string> arguments)
    {
        if (arguments.Count == 0)
            return $"{AzureDevOpsUrl}/{_organization}/{_project}/_apis/{api}";

        string queryString = string.Join("&", arguments.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{AzureDevOpsUrl}/{_organization}/{_project}/_apis/{api}?{queryString}";
    }

    async Task<AzureDevOpsList<Commit>> ExecuteCommitsAsync(string api, Dictionary<string, string> arguments)
    {
        string response = await CallAsync(api, arguments);
        return DeserializeCommitList(response);
    }

    AzureDevOpsList<Commit> DeserializeCommitList(string response) =>
        JsonSerializer.Deserialize(response, SourceGenerationContext.Default.AzureDevOpsListCommit) ?? throw new Exception("Could not deserialize the response.");

    Task<AzureDevOpsList<Commit>> GetCommits(CommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Arguments["searchCriteria.compareVersion.versionType"] = "branch";
        options.Arguments["searchCriteria.compareVersion.version"] = options.BranchName;
        options.Arguments["searchCriteria.itemVersion.versionType"] = "commit";
        options.Arguments["searchCriteria.itemVersion.version"] = options.FromCommitId;
        options.Arguments["searchCriteria.$top"] = "100";

        return ExecuteCommitsAsync($"git/repositories/{options.Repo}/commits", options.Arguments);
    }

    public Task<AzureDevOpsList<Commit>> GetCommitsAfterCommit(string repository, string fromCommitId, string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCommitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        CommitOptions options = new()
        {
            Repo = repository,
            FromCommitId = fromCommitId,
            BranchName = branchName
        };

        return GetCommits(options);
    }
}
