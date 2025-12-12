using System;
using System.Collections.Generic;

namespace InfrastructureTools.Connectors.AzureDevOps;

public class Commit
{
    public Commit() { }

    public string CommitId { get; set; } = string.Empty;
    public CommitPerson Author { get; set; } = new();
    public CommitPerson Committer { get; set; } = new();
    public string Comment { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;

    public override string ToString()
    {
        return $@"CommitId: {CommitId}
Author: {Author}
Commiter: {Committer}
Comment: {Comment}
RemoteUrl: {RemoteUrl}
";
    }
}

public class CommitPerson
{
    public CommitPerson() { }

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }

    public override string ToString()
    {
        return @$"
    Name: {Name}
    Email: {Email}
    Date: {Date}";
    }
}

/*
All:
    git/repositories/{repositoryId}/commits?
        searchCriteria.$skip={searchCriteria.$skip}&
        searchCriteria.$top={searchCriteria.$top}&
        searchCriteria.author={searchCriteria.author}&
        searchCriteria.compareVersion.version={searchCriteria.compareVersion.version}&
        searchCriteria.compareVersion.versionOptions={searchCriteria.compareVersion.versionOptions}&
        searchCriteria.compareVersion.versionType={searchCriteria.compareVersion.versionType}&
        searchCriteria.excludeDeletes={searchCriteria.excludeDeletes}&
        searchCriteria.fromCommitId={searchCriteria.fromCommitId}&
        searchCriteria.fromDate={searchCriteria.fromDate}&
        searchCriteria.historyMode={searchCriteria.historyMode}&
        searchCriteria.ids={searchCriteria.ids}&
        searchCriteria.includeLinks={searchCriteria.includeLinks}&
        searchCriteria.includePushData={searchCriteria.includePushData}&
        searchCriteria.includeUserImageUrl={searchCriteria.includeUserImageUrl}&
        searchCriteria.includeWorkItems={searchCriteria.includeWorkItems}&
        searchCriteria.itemPath={searchCriteria.itemPath}&
        searchCriteria.itemVersion.version={searchCriteria.itemVersion.version}&
        searchCriteria.itemVersion.versionOptions={searchCriteria.itemVersion.versionOptions}&
        searchCriteria.itemVersion.versionType={searchCriteria.itemVersion.versionType}&
        searchCriteria.showOldestCommitsFirst={searchCriteria.showOldestCommitsFirst}&
        searchCriteria.toCommitId={searchCriteria.toCommitId}&
        searchCriteria.toDate={searchCriteria.toDate}&
        searchCriteria.user={searchCriteria.user}
Single:
    git/repositories/{repositoryId}/commits/{commitId}?
        changeCount={changeCount}
 */
public class CommitOptions
{
    public string Repo { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public int Top { get; set; } = -1;
    
    // Commit ID filtering
    public required string FromCommitId { get; set; }
    public string? ToCommitId { get; set; }
    
    public Dictionary<string, string> Arguments { get; set; } = new Dictionary<string, string>();
}
