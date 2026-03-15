# DotAgent

DotAgent is an autonomous coding agent built with .NET 10 and Docker. It features a dual-mode workflow (Plan and Execute) and integrates with Context7 for documentation lookups and a secure workspace sandbox.

## Features

- Plan Mode: Exploration and architectural planning with mandatory user approval and clarifying questions.
- Execute Mode: Step-by-step implementation of approved plans with self-healing loops.
- Workspace Sandbox: All execution is restricted to the /workspace directory to prevent accidental changes to the host system.
- Context7 Integration: Access up-to-date documentation for any library directly within the agent's environment.
- State Tracking: Resumable sessions with file-level progress tracking.

## Prerequisites

- Docker and Docker Compose
- .NET 10 SDK (for local development)
- Node.js

## Configuration

The application supports multiple LLM providers (ZhipuAI and Groq) with automatic fallback. Create a `.env` file in the root directory:

```env
# ZhipuAI (glm-4.7-flash)
ZHIPU_API_KEY=your_zhipu_key
ZHIPU_MODEL=glm-4.7-flash

# Groq (Llama-3.3-70b-versatile)
GROQ_API_KEY=your_groq_key
GROQ_MODEL=llama-3.3-70b-versatile

# Documentation Lookups (Get a key at context7.com)
CONTEXT7_API_KEY=your_key_optional

# Global Settings
WORKSPACE=/workspace
```

## Getting Started

### Using Docker (Recommended)

The easiest way to run DotAgent is using Docker Compose, which sets up the full toolchain (Python, Node, uv, Context7).

1. Build the container:
   ```bash
   docker compose build
   ```

2. Start a session:
   ```bash
   docker compose run --rm dot-agent
   ```

### Local Development

If you prefer to run locally, ensure you have the .NET 10 runtime installed.

1. Build the project:
   ```bash
   dotnet build
   ```

2. Run the executable:
   ```bash
   dotnet run
   ```

## Usage

1. Start the agent and enter a session title.
2. Initialize a task using `/plan <your feature or bugfix>`.
3. Answer any clarifying questions the agent asks.
4. Once the plan is generated and approved, the agent will switch to Execute mode.
5. Watch the agent work through the steps, creating files and running commands automatically.

## Project Structure

- `Orchestrator/`: Core logic for the Plan/Execute loops and sandbox enforcement.
- `Services/`: File manipulation tools, LLM integration, and shell execution.
- `Prompts/`: System instructions that define the agent's behavior and constraints.
- `Models/`: Data structures for session and state management.
