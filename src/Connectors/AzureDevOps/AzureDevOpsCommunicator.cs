using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace InfrastructureTools.Connectors.AzureDevOps;

public class AzureDevOpsCommunicator : IDisposable
{
    private readonly TokenCredential _credential;
    private readonly string _organization;
    private readonly string _project;
    private readonly HttpClient _httpClient;
    readonly JsonSerializerOptions _options;

    internal string AzureDevOpsUrl => "https://dev.azure.com";
    internal string Organization => _organization;
    internal string Project => _project;

    public AzureDevOpsCommunicator(string organization, string project, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);
        ArgumentException.ThrowIfNullOrEmpty(project);
        ArgumentNullException.ThrowIfNull(credential);

        _organization = organization;
        _project = project;
        _credential = credential;

        MediaTypeWithQualityHeaderValue jsonMediaType = new("application/json");

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(jsonMediaType);

        _options = new JsonSerializerOptions()
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) },
            PropertyNameCaseInsensitive = true,
        };
    }

    /// <summary>
    /// Releases unmanaged resources.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Executes an Azure DevOps API query.
    /// </summary>
    /// <returns>The response content of the query.</returns>
    /// <param name="api">Represents the value of the API to invoke.</param>
    public Task<string> CallAsync(string api, Dictionary<string, string> arguments)
    {
        string query = GetQueryUrl(api, arguments);
        return CallAsync(query);
    }

    private async Task<string> CallAsync(string query)
    {
        // Get and set the bearer token
        // 499b84ac-1321-427f-aa17-267ca6975798 is Azure DevOps's registered application ID in Azure AD
        // /.default suffix requests all default permissions for the Azure DevOps resource
        var tokenRequestContext = new TokenRequestContext(new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" });
        var token = await _credential.GetTokenAsync(tokenRequestContext, default);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using HttpResponseMessage response = await _httpClient.GetAsync(query);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public string GetQueryUrl(string api, Dictionary<string, string> arguments)
    {
        string joinedArguments = "";
        bool first = true;
        foreach ((string key, string value) in arguments)
        {
            string separator = "&";
            if (first)
            {
                separator = "?";
                first = false;
            }
            joinedArguments += $"{separator}{key}={value}";
        }

        return $"{AzureDevOpsUrl}/{_organization}/{_project}/_apis/{api}{joinedArguments}";
    }

    public async Task<AzureDevOpsList<T>> ExecuteAsync<T>(string api, Dictionary<string, string> arguments)
    {
        string response = await CallAsync(api, arguments);
        return DeserializeAsList<T>(response);
    }

    public AzureDevOpsList<T> DeserializeAsList<T>(string response) => Deserialize<AzureDevOpsList<T>>(response);

    public T Deserialize<T>(string response) =>
        JsonSerializer.Deserialize<T>(response, _options) ?? throw new Exception("Could not deserialize the response.");

    public Task<AzureDevOpsList<BuildDefinition>> GetBuildDefinitions(BuildDefinitionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.BuildDefinitionName))
        {
            options.Arguments["name"] = options.BuildDefinitionName;
        }

        return ExecuteAsync<BuildDefinition>("build/definitions", options.Arguments);
    }

    public Task<AzureDevOpsList<Build>> GetBuilds(BuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string api = options.BuildId != null && options.BuildId.HasValue ? $"build/builds/{options.BuildId.Value}" : "build/builds";
        
        return ExecuteAsync<Build>(api, options.Arguments);
    }

    public Task<AzureDevOpsList<Pipeline>> GetPipelines(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string api = options.PipelineId != null && options.PipelineId.HasValue ? $"pipelines/{options.PipelineId}" : "pipelines";
        
        return ExecuteAsync<Pipeline>(api, options.Arguments);
    }

    public Task<AzureDevOpsList<Commit>> GetCommits(CommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Set version type and version if branch name is specified
        if (!string.IsNullOrWhiteSpace(options.BranchName))
        {
            options.Arguments["searchCriteria.itemVersion.versionType"] = "branch";
            options.Arguments["searchCriteria.itemVersion.version"] = options.BranchName;
        }
        
        // Set from/to commit IDs for filtering
        if (!string.IsNullOrWhiteSpace(options.FromCommitId))
        {
            options.Arguments["searchCriteria.fromCommitId"] = options.FromCommitId;
        }
        if (!string.IsNullOrWhiteSpace(options.ToCommitId))
        {
            options.Arguments["searchCriteria.toCommitId"] = options.ToCommitId;
        }

        return ExecuteAsync<Commit>($"git/repositories/{options.Repo}/commits", options.Arguments);
    }

    /// <summary>
    /// Gets all commits after a specific commit ID.
    /// </summary>
    /// <param name="repository">Repository name or ID</param>
    /// <param name="fromCommitId">Starting commit ID (SHA)</param>
    /// <param name="toCommitId">Ending commit ID (optional, defaults to HEAD)</param>
    /// <returns>List of commits after the specified commit</returns>
    public Task<AzureDevOpsList<Commit>> GetCommitsAfterCommit(string repository, string fromCommitId, string? toCommitId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCommitId);

        CommitOptions options = new()
        {
            Repo = repository,
            FromCommitId = fromCommitId,
            ToCommitId = toCommitId
        };

        return GetCommits(options);
    }

    public Task<AzureDevOpsList<PullRequest>> GetPullRequests(PullRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Repo);
        ArgumentException.ThrowIfNullOrEmpty(options.TargetBranch);
        if(!Enum.IsDefined(options.Status))
        {
            throw new ArgumentOutOfRangeException("PullRequestOptions.Status");
        }

        string prefix = "refs/heads/";
        if (!options.TargetBranch.StartsWith(prefix))
        {
            options.TargetBranch = $"{prefix}{options.TargetBranch}";
        }
        options.Arguments["searchCriteria.targetRefName"] = options.TargetBranch;

        string str = options.Status.ToString();
        options.Arguments["searchCriteria.status"] = char.ToLower(str[0]) + str[1..];

        return ExecuteAsync<PullRequest>($"git/repositories/{options.Repo}/pullrequests", options.Arguments);
    }

    public Task<AzureDevOpsList<Artifact>> GetArtifacts(ArtifactsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ArtifactName))
        {
            options.Arguments["artifactName"] = options.ArtifactName;
        }

        return ExecuteAsync<Artifact>($"build/builds/{options.BuildNumber}/artifacts", options.Arguments);
    }

    public Task<AzureDevOpsList<Artifact>> GetArtifactsFromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        Match match = Regex.Match(url, @$"{AzureDevOpsUrl}{Organization}/{Project}/_build/results?.*buildId=(?<BuildId>\d+).*");
        if (!match.Success)
        {
            throw new Exception("Incorrect URL format.");
        }

        ArtifactsOptions options = new()
        {
            BuildNumber = int.Parse(match.Groups["BuildId"].Value)
        };

        return GetArtifacts(options);
    }
}