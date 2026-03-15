#!/bin/bash
# Setup Context7 CLI for DotAgent
# This installs the Context7 MCP CLI tool globally

set -e

echo "Installing Context7 CLI..."

# Check if npm is available
if ! command -v npm &> /dev/null; then
    echo "Error: npm is not installed. Please install Node.js first."
    exit 1
fi

# Install ctx7 globally
npm install -g @upstash/context7-mcp

echo ""
echo "Context7 installed successfully!"
echo ""
echo "Usage in DotAgent Plan mode:"
echo "  ctx7 library <name> <query>   — Search for a library"
echo "  ctx7 docs <libraryId> <query> — Fetch library docs"
echo ""
echo "Optional: Get an API key for higher rate limits at:"
echo "  https://context7.com/dashboard"
echo ""
echo "Set it in your .env file as: CONTEXT7_API_KEY=your_key_here"
