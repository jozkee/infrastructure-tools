# CommitCollectorAzDO

A C# console application that collects and filters commits from Azure DevOps based on commit ranges, with optional GitHub integration for PR and approver details.

## Features

This tool retrieves commits after a specific commit ID (or between two commit IDs) and applies intelligent filtering to identify commits that are relevant for release notes or documentation purposes.

### GitHub Integration (Optional)

When GitHub organization and repository are specified, the tool will:
- Extract PR numbers from commit messages
- Fetch PR details from GitHub (title, URL, author)
- Retrieve PR approvers from GitHub reviews
- Display this information in the output table

**Prerequisites for GitHub integration:**
- Install GitHub CLI: `winget install GitHub.cli`
- Authenticate once: `gh auth login`

The tool uses `gh auth token` to obtain your GitHub token automatically.

### Filtering Logic

Commits are **skipped** if they meet any of these criteria:

- **Bot authors**: Commits by `dotnet-maestro[bot]` or `azure-pipelines[bot]`
- **Forbidden strings**: Commits with titles containing:
  - "Update dependencies from"
  - "Merge branch"
- **Forbidden patterns**: Commits matching patterns like:
  - "Merge pull request #\d+ from"
- **Infrastructure-only changes**: Commits that only modify infrastructure files (`.csproj`, `.props`, `.json`, `.yml`, etc.)
- **Test-only changes**: Commits that only modify test files

### Output

The tool generates two markdown tables:

1. **Included Commits**: Commits that passed all filters, showing:
   - PR/Commit link
   - Author name
   - Comments (placeholder)
   - Validation status (placeholder)

2. **Skipped Commits**: Commits that were filtered out, showing:
   - Reason for skipping
   - Commit title

## Prerequisites

- .NET 9.0 SDK or later
- One of the following authentication methods:
  - **Azure CLI** (recommended): Run `az login` before using the tool
  - **Personal Access Token (PAT)**: With Code (Read) permissions
  - **Managed Identity**: When running in Azure environments
  - Other authentication methods supported by `DefaultAzureCredential`

## Authentication

The tool uses **DefaultAzureCredential** by default, which tries multiple authentication methods in order:
1. Environment variables
2. Managed Identity
3. Azure CLI (`az login`)
4. Visual Studio
5. Azure PowerShell
6. And more...

### Recommended: Azure CLI

```bash
# Login with Azure CLI (one-time setup)
az login

# Now you can use the tool without specifying credentials
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- -org "myorg" -project "myproject" -repo "myrepo" -pr 12345
```

### Alternative: Personal Access Token

```bash
# Using environment variable
$env:AZDO_PAT = "your-personal-access-token"
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- -org "myorg" -project "myproject" -repo "myrepo" -pr 12345

# Or pass directly
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- -org "myorg" -project "myproject" -repo "myrepo" -pr 12345 -pat "your-pat"
```

### Build the project

```bash
dotnet build src/CommitCollectorAzDO/CommitCollectorAzDO.csproj
```

## Usage

```bash
# Basic usage - get all commits after a specific commit ID
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- \
  -org "myorg" \
  -project "myproject" \
  -repo "myrepo" \
  -from-commit "abc123def456..."

# With GitHub integration - fetches PR and approver details
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- \
  -org "myorg" \
  -project "myproject" \
  -repo "myrepo" \
  -from-commit "abc123def456..." \
  -github-org "dotnet" \
  -github-repo "runtime"

# With commit range
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- \
  -org "myorg" \
  -project "myproject" \
  -repo "myrepo" \
  -from-commit "abc123" \
  -to-commit "def456" \
  -github-org "dotnet" \
  -github-repo "runtime"
```
### Command-line options

| Option | Alias | Required | Description |
|--------|-------|----------|-------------|
| `-org` | `-o` | Yes | Azure DevOps organization name |
| `-project` | `-p` | Yes | Azure DevOps project name |
| `-repo` | `-r` | Yes | Repository name or ID |
| `-from-commit` | `-fc` | Yes | Starting commit ID (SHA) to collect commits from |
| `-to-commit` | `-tc` | No | Ending commit ID (SHA) for commit collection (defaults to HEAD) |
| `-github-org` | `-go` | No | GitHub organization name (for PR details) |
| `-github-repo` | `-gr` | No | GitHub repository name (for PR details) |
| `--debug` | `-d` | No | Break execution to attach debugger |

### Example

```bash
# Basic usage with Azure CLI authentication
az login
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- `
  -org "dnceng" `
  -project "internal" `
  -repo "dotnet-runtime" `
  -from-commit "a2266c728f63a494ccb6786d794da2df135030be"

# With GitHub integration to fetch PR and approver details
gh auth login  # One-time setup
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- `
  -org "dnceng" `
  -project "internal" `
  -repo "dotnet-runtime" `
  -from-commit "a2266c728f63a494ccb6786d794da2df135030be" `
  -github-org "dotnet" `
  -github-repo "runtime"

# Get commits between two specific commits
dotnet run --project src/CommitCollectorAzDO/CommitCollectorAzDO.csproj -- `
  -org "dnceng" `
  -project "internal" `
  -repo "dotnet-runtime" `
  -from-commit "a2266c728f63a494ccb6786d794da2df135030be" `
  -to-commit "def456789..." `
  -github-org "dotnet" `
  -github-repo "runtime"
```

## Output Example

### Without GitHub Integration

```
| Commit | Author / Approvers | Comments | Validation status |
| --- | --- | --- | --- |
| [Fix serialization issue](https://dev.azure.com/...) | John Doe | | |
| [Add validation logic](https://dev.azure.com/...) | Jane Smith | | |
```

### With GitHub Integration

```
| Commit | Author / Approvers | Comments | Validation status |
| --- | --- | --- | --- |
| [Fix serialization issue](https://github.com/.../pull/12345) | johndoe / janereview, bobapprover | | |
| [Add validation logic](https://github.com/.../pull/12346) | janesmith / johndoe | | |
```

The tool shows:
- **Without GitHub**: Links to AzDO commits, shows AzDO commit author
- **With GitHub**: Links to GitHub PRs, shows GitHub PR author and reviewers who approved
| --- | --- |
| All infra files | Update project files |
| All test files | Add unit tests for serialization |
| author: azure-pipelines[bot] | Merge branch 'main' |

-----
```

## Project Structure

- `CommitCollectorAzDO.cs` - Main logic for filtering and processing commits
- `Program.cs` - CLI interface using System.CommandLine
- `CommitCollectorAzDO.csproj` - Project file with dependencies

## Dependencies

- `InfrastructureTools.Connectors.AzureDevOps` - Azure DevOps API communication
- `InfrastructureTools.MarkdownTable` - Markdown table generation
- `System.CommandLine` - Command-line argument parsing

## Customization

You can modify the filtering criteria by editing these arrays in [CommitCollectorAzDO.cs](CommitCollectorAzDO.cs):

- `InfraExtensions` - File extensions considered infrastructure
- `ForbiddenStrings` - Strings that trigger filtering
- `ForbiddenPatterns` - Regex patterns that trigger filtering
- `StringsToTrim` - Prefixes to remove from commit titles
- `StringPatternsToTrim` - Regex patterns to remove from commit titles
