using InfrastructureTools.Shared;
using System;
using System.CommandLine;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfrastructureTools.CommitCollectorAzDO;

public static class Program
{
    public static async Task Main(string[] args)
    {
        RootCommand rootCommand = new("commit-collector-azdo - Collect and filter commits from Azure DevOps commit ranges");

        Option<string> optionFromCommit = new(["-from-commit", "-fc"], "The starting commit ID (SHA) to collect commits from.")
        {
            IsRequired = true
        };
        Option<string?> optionToCommit = new(["-to-commit", "-tc"], "The ending commit ID (SHA) for commit collection (optional, defaults to HEAD).");
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
        Option<string?> optionGitHubRepo = new(["-github-repo", "-gr"], "The GitHub repository name (optional, for fetching PR details).");        Option<string?> optionBranch = new(["--branch", "-b"], "The branch name to filter commits (optional, defaults to all branches).");        Option<bool> optionDebug = new(["--debug", "-d"], () => false, "Break execution at the beginning to attach a debugger.");

        rootCommand.Add(optionFromCommit);
        rootCommand.Add(optionToCommit);
        rootCommand.Add(optionOrg);
        rootCommand.Add(optionProject);
        rootCommand.Add(optionRepo);
        rootCommand.Add(optionGitHubOrg);
        rootCommand.Add(optionGitHubRepo);
        rootCommand.Add(optionBranch);
        rootCommand.Add(optionDebug);

        rootCommand.SetHandler(async (context) =>
        {
            var fromCommit = context.ParseResult.GetValueForOption(optionFromCommit);
            var toCommit = context.ParseResult.GetValueForOption(optionToCommit);
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
            if (!string.IsNullOrWhiteSpace(toCommit))
            {
                ConsoleLog.WriteInfo($"To Commit: {toCommit}");
            }
            if (!string.IsNullOrWhiteSpace(branch))
            {
                ConsoleLog.WriteInfo($"Branch: {branch}");
            }
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

            // Validate required parameters
            if (string.IsNullOrWhiteSpace(org))
            {
                ConsoleLog.WriteError("Error: -org is required.");
                return;
            }
            if (string.IsNullOrWhiteSpace(project))
            {
                ConsoleLog.WriteError("Error: -project is required.");
                return;
            }
            if (string.IsNullOrWhiteSpace(repo))
            {
                ConsoleLog.WriteError("Error: -repo is required.");
                return;
            }

            CommitCollectorAzDO collector = CommitCollectorAzDO.Create(org, project, repo, githubOrg, githubRepo);
            await collector.RunAsync(fromCommit!, toCommit, branch);
        });

        await rootCommand.InvokeAsync(args);
    }
}
