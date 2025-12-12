# CommitCollectorAzDO - Single File Version

This is a single-file port of the CommitCollectorAzDO tool, following the [file-based programs pattern](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/tutorials/file-based-programs) in C#.

## What's Different?

- **Single File**: All code is in `Program.cs` - no .csproj needed!
- **Self-Contained**: All dependencies are either inlined or referenced via `#r "nuget:..."` directives
- **Standalone**: Can be run directly with `dotnet run Program.cs`
- **Top-Level Statements**: Uses modern C# top-level program structure
- **Same Functionality**: Maintains all features of the original multi-file version

## Usage

Run the standalone .cs file directly:

```powershell
dotnet run Program.cs -- -org "dnceng" -project "internal" -repo "dotnet-runtime" -github-org "dotnet" -github-repo "runtime" -from-commit "abc123" --branch "release/8.0"
```

## Options

- `-org`, `-o`: Azure DevOps organization name (required)
- `-project`, `-p`: Azure DevOps project name (required)
- `-repo`, `-r`: Repository name or ID (required)
- `-from-commit`, `-fc`: Starting commit ID (SHA) (required)
- `-to-commit`, `-tc`: Ending commit ID (SHA) (optional)
- `--branch`, `-b`: Branch name to filter commits (optional)
- `-github-org`, `-go`: GitHub organization name (optional, for PR details)
- `-github-repo`, `-gr`: GitHub repository name (optional, for PR details)
- `--debug`, `-d`: Break execution to attach debugger (optional)

## How It Works

The single-file version combines:
1. Top-level statements for the command-line interface
2. Class definitions at the file scope (no namespace)
3. All helper classes in the same file

This follows the C# 9.0+ file-based programs pattern where you can have executable statements at the top of the file, followed by type definitions.
