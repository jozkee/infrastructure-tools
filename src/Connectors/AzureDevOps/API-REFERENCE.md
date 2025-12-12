# Azure DevOps REST API Reference

This document provides quick links and examples for the Azure DevOps REST APIs used in this project.

## Official Documentation

**Main REST API Reference**: https://learn.microsoft.com/en-us/rest/api/azure/devops/

### Specific API Endpoints

1. **Git - Commits**  
   https://learn.microsoft.com/en-us/rest/api/azure/devops/git/commits
   - List commits
   - Get commit details
   - Get commit changes

2. **Git - Pull Requests**  
   https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-requests
   - List PRs
   - Get PR details
   - Get PR commits

3. **Build**  
   https://learn.microsoft.com/en-us/rest/api/azure/devops/build
   - List builds
   - Get build details

## API Version

Current API version used: **7.1**

## Base URL Format

```
https://dev.azure.com/{organization}/{project}/_apis/{api-path}?api-version=7.1
```

## Authentication

### Using Personal Access Token (PAT)

```http
Authorization: Basic {base64(:{PAT})}
```

### Using Azure AD Token (Bearer)

```http
Authorization: Bearer {access_token}
```

**Azure DevOps Resource ID**: `499b84ac-1321-427f-aa17-267ca6975798`

## Common API Examples

### Get Commits from a Pull Request

```http
GET https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}/commits?api-version=7.1
```

### Get Commits After a Specific Commit

```http
GET https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/commits?searchCriteria.fromCommitId={fromCommitId}&api-version=7.1
```

### Get Commits Between Two Commits

```http
GET https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/commits?searchCriteria.fromCommitId={fromCommitId}&searchCriteria.toCommitId={toCommitId}&api-version=7.1
```

### Get Commit Changes

```http
GET https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/commits/{commitId}/changes?api-version=7.1
```

### Get Pull Request Details

```http
GET https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/pullrequests/{pullRequestId}?api-version=7.1
```

## Query Parameters for Commits

| Parameter | Type | Description |
|-----------|------|-------------|
| `searchCriteria.fromCommitId` | string | Starting commit ID |
| `searchCriteria.toCommitId` | string | Ending commit ID |
| `searchCriteria.itemVersion.version` | string | Branch name |
| `searchCriteria.itemVersion.versionType` | string | `branch`, `tag`, or `commit` |
| `searchCriteria.$top` | int | Maximum number of commits |
| `searchCriteria.$skip` | int | Number of commits to skip |
| `searchCriteria.fromDate` | datetime | Start date filter |
| `searchCriteria.toDate` | datetime | End date filter |
| `searchCriteria.author` | string | Filter by author |

## Response Format

All list responses follow this format:

```json
{
  "count": 2,
  "value": [
    {
      // Individual item data
    },
    {
      // Individual item data
    }
  ]
}
```

## Rate Limiting

Azure DevOps enforces rate limits:
- **Personal Access Tokens**: Generally higher limits
- **Service Principals**: Application-specific limits

## Testing APIs with PowerShell

```powershell
$org = "your-org"
$project = "your-project"
$pat = "your-pat"
$base64AuthInfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$pat"))

$headers = @{
    Authorization = "Basic $base64AuthInfo"
    "Content-Type" = "application/json"
}

$uri = "https://dev.azure.com/$org/$project/_apis/git/repositories/{repoId}/refs?filter=refs/tags&api-version=7.1"

Invoke-RestMethod -Uri $uri -Headers $headers -Method Get
```

## Testing APIs with curl

```bash
ORG="your-org"
PROJECT="your-project"
PAT="your-pat"

curl -u ":$PAT" \
  "https://dev.azure.com/$ORG/$PROJECT/_apis/git/repositories/{repoId}/refs?filter=refs/tags&api-version=7.1"
```

## Additional Resources

- **Azure DevOps REST API Overview**: https://learn.microsoft.com/en-us/rest/api/azure/devops/?view=azure-devops-rest-7.1
- **Authentication Guide**: https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate
- **API Rate Limits**: https://learn.microsoft.com/en-us/azure/devops/integrate/concepts/rate-limits
