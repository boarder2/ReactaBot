# Copilot Instructions for ReactaBot

This repository contains a Discord bot application built with .NET 8 that tracks message reactions.

## Project Context
- Discord bot application using Discord.NET
- .NET 8 platform
- SQLite database for storage
- Uses Cronos for scheduling functionality

## Coding Standards
- Use C# with .NET 8 features
- Utilize primary constructors when possible
- Implement dependency injection for services
- Use SQLite for data persistence
- Use tabs for indentation
- Avoid code duplication - prefer shared methods/classes
- Follow Microsoft's recommended practices for .NET coding standards

## Project Structure
- `Commands/` - Discord slash command implementations
- `Common/` - Shared utilities and embed handlers
- `Extensions/` - Extension methods for Discord.NET and other utilities
- `Health/` - Health check implementations
- `Models/` - Data models
- `Services/` - Core services including scheduling and reactions handling

## Best Practices
- Never use new NuGet packages unless explicitly asked
- Never update NuGet packages unless explicitly asked

## Documentation
- Ensure the `Schema.md` file is updated when anything about the database persistence or schema changes
