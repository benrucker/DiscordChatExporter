# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Build the solution
dotnet build

# Build without formatting (faster)
dotnet build -p:CSharpier_Bypass=true

# Run tests (requires DISCORD_TOKEN environment variable or user secret)
dotnet test

# Run a specific test
dotnet test --filter "FullyQualifiedName~HtmlContentSpecs"

# Publish CLI (self-contained, trimmed)
dotnet publish DiscordChatExporter.Cli --configuration Release --runtime win-x64 --self-contained
```

## Code Formatting

CSharpier runs automatically on build. To format manually:
```bash
dotnet build -t:CSharpierFormat
```

## Project Structure

- **DiscordChatExporter.Core** - Core library containing Discord API client and export logic
- **DiscordChatExporter.Cli** - Command-line interface using CliFx
- **DiscordChatExporter.Gui** - Desktop GUI using Avalonia (MVVM pattern)
- **DiscordChatExporter.Cli.Tests** - Integration tests using xUnit

## Architecture

### Core Library (`DiscordChatExporter.Core`)

- `Discord/DiscordClient.cs` - HTTP client for Discord API with rate limiting
- `Discord/Data/` - Data models (Channel, Message, Guild, User, etc.)
- `Exporting/ChannelExporter.cs` - Orchestrates channel export process
- `Exporting/ExportRequest.cs` - Configuration for an export operation
- `Exporting/ExportContext.cs` - Runtime context during export (caches members, roles)
- `Exporting/*MessageWriter.cs` - Format-specific writers (Html, Json, Csv, PlainText)
- `Exporting/Filtering/` - Message filter expression parser
- `Exporting/Partitioning/` - Output file partitioning logic
- `Markdown/` - Discord markdown parser

### CLI (`DiscordChatExporter.Cli`)

Commands use CliFx and follow an inheritance pattern:
- `DiscordCommandBase` - Base with token/rate-limit options, creates `DiscordClient`
- `ExportCommandBase` - Adds export options (format, output, filters, etc.)
- Concrete commands: `ExportChannelsCommand`, `ExportGuildCommand`, `ExportAllCommand`, etc.

### Tests

Tests require a `DISCORD_TOKEN` environment variable or user secret pointing to a bot token with access to a test server. Tests export real Discord channels and verify the output.

Test infrastructure:
- `Infra/ExportWrapper.cs` - Caches exports to avoid redundant API calls
- `Infra/ChannelIds.cs` - Pre-defined test channel IDs
- `Infra/Secrets.cs` - Token configuration from env vars or user secrets

## Key Patterns

- **Snowflake** - Discord's ID type, can be parsed from ID string or date
- **ExportFormat** - Enum: HtmlDark, HtmlLight, PlainText, Json, Csv
- Export flow: Command → ExportRequest → ChannelExporter → MessageExporter → MessageWriter
- Razor templates (`.cshtml`) generate HTML output

## Configuration

- .NET 10 with C# preview features (`LangVersion=preview`)
- Nullable reference types enabled
- Warnings treated as errors
- Trimming enabled for published builds
