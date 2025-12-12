using InfrastructureTools.Shared;
using InfrastructureTools.Connectors.AzureDevOps;
using InfrastructureTools.MarkdownTable;
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
using System.Net.Http;
using System.Net.Http.Headers;

namespace InfrastructureTools.CommitCollectorAzDO;

public partial class CommitCollectorAzDO
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
    private readonly string _organization;
    private readonly string _project;
    private readonly string _repository;
    private readonly string? _githubOrg;
    private readonly string? _githubRepo;
    private readonly GitHubCommitFetcher? _githubFetcher;

    public AzureDevOpsCommunicator Communicator { get; }

    private CommitCollectorAzDO(AzureDevOpsCommunicator communicator, string organization, string project, string repository, string? githubOrg, string? githubRepo, GitHubCommitFetcher? githubFetcher)
    {
        Communicator = communicator;
        _organization = organization;
        _project = project;
        _repository = repository;
        _githubOrg = githubOrg;
        _githubRepo = githubRepo;
        _githubFetcher = githubFetcher;
        _errors = new List<string>();
    }

    public static CommitCollectorAzDO Create(string organization, string project, string repository, string? githubOrg = null, string? githubRepo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        // Use DefaultAzureCredential (tries AzureCliCredential, ManagedIdentity, etc.)
        ConsoleLog.WriteInfo("Using DefaultAzureCredential for authentication (AzureCliCredential, ManagedIdentity, etc.)");
        TokenCredential credential = new DefaultAzureCredential();
        AzureDevOpsCommunicator communicator = new AzureDevOpsCommunicator(organization, project, credential);
        
        // Initialize GitHub fetcher if GitHub org/repo provided
        GitHubCommitFetcher? githubFetcher = null;
        if (!string.IsNullOrWhiteSpace(githubOrg) && !string.IsNullOrWhiteSpace(githubRepo))
        {
            ConsoleLog.WriteInfo("GitHub repository specified. Will fetch PR and approver information from GitHub.");
            githubFetcher = new GitHubCommitFetcher(githubOrg, githubRepo);
        }
        
        return new CommitCollectorAzDO(communicator, organization, project, repository, githubOrg, githubRepo, githubFetcher);
    }

    public async Task RunAsync(string fromCommitId, string? toCommitId = null)
    {
        ConsoleLog.WriteInfo($"Fetching commits after '{fromCommitId[..8]}...'{(toCommitId != null ? $" up to '{toCommitId[..8]}...'" : "")}...");
        
        AzureDevOpsList<Commit> commits = await Communicator.GetCommitsAfterCommit(_repository, fromCommitId, toCommitId);
        
        ConsoleLog.WriteSuccess($"Found {commits.Count} commits");

        List<(Commit, CommitChanges)> included = new();
        List<(Commit, string)> skipped = new();

        foreach (Commit commit in commits.Value)
        {
            await ProcessCommitAsync(commit, included, skipped);
        }

        PrintIncludedTable(included, fromCommitId, toCommitId);
        PrintSkippedTable(skipped);
        PrintErrors();
    }

    public async Task ProcessCommitAsync(Commit commit, List<(Commit, CommitChanges)> included, List<(Commit, string)> skipped)
    {
        // Get commit changes
        string api = $"git/repositories/{_repository}/commits/{commit.CommitId}/changes";
        string response = await Communicator.CallAsync(api, new Dictionary<string, string>());
        CommitChanges changes = JsonSerializer.Deserialize<CommitChanges>(response, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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

    private async void PrintIncludedTable(List<(Commit, CommitChanges)> included, string? fromCommitId = null, string? toCommitId = null)
    {
        var table = new MarkdownTableBuilder().WithHeader("Commit", "Author / Approvers", "Comments", "Validation status");
        foreach ((Commit commit, CommitChanges changes) in included)
        {
            string commitUrl = $"https://dev.azure.com/{_organization}/{_project}/_git/{_repository}/commit/{commit.CommitId}";
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
        string range = toCommitId != null ? $"from {fromCommitId![..8]}... to {toCommitId[..8]}..." : $"after {fromCommitId![..8]}...";
        ConsoleLog.WriteSuccess($"Commits {range}:");
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
public class CommitChanges
{
    public List<CommitChange>? Changes { get; set; }
    public int ChangeCounts { get; set; }
}

public class CommitChange
{
    public CommitItem? Item { get; set; }
    public string? ChangeType { get; set; }
}

public class CommitItem
{
    public string? ObjectId { get; set; }
    public string? Path { get; set; }
    public bool IsFolder { get; set; }
}

// GitHub integration classes
public class GitHubCommitInfo
{
    public string PrUrl { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public List<string> Approvers { get; set; } = new();
}

public class GitHubTokenProvider
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

public class GitHubCommitFetcher : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _org;
    private readonly string _repo;
    private readonly string _token;

    public GitHubCommitFetcher(string org, string repo)
    {
        _org = org;
        _repo = repo;
        _token = GitHubTokenProvider.GetTokenAsync().GetAwaiter().GetResult();
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CommitCollectorAzDO", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
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
