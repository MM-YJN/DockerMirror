#!/bin/bash
set -e

export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH

echo 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$HOME/.dapr/bin:$PATH"' >> ~/.bashrc
echo 'export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:$SSL_CERT_DIR"' >> ~/.bashrc

# Install the Aspire CLI, which is required to build and run the application.
echo "Installing Aspire CLI..."
curl --proto "=https" --tlsv1.2 -sSf -L https://aspire.dev/install.sh | bash

# Set up user secrets persistence
echo "User secrets: fixing user permissions"

# own the mounted volume
if command -v sudo > /dev/null; then
    sudo chown -R "$(id -u):$(id -g)" /dc/dotnet-usersecrets
else
    chown -R "$(id -u):$(id -g)" /dc/dotnet-usersecrets
fi

echo "User secrets: setting up user secrets persistence"

# ensure the .microsoft directory exists
mkdir -p "${HOME}/.microsoft"

# create symbolic link for user secrets storage
ln -sf /dc/dotnet-usersecrets "${HOME}/.microsoft/usersecrets"

# Configure git
echo "Configuring git to use credential helper with HTTP path support..."
git config --global core.autocrlf input
git config --global credential.useHttpPath true

# Install OpenCode
echo "Installing OpenCode CLI..."
npm install -g opencode-ai
mkdir -p "${HOME}/.config/opencode/prompts"
SCRIPT_DIR=$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")
cp "$SCRIPT_DIR/opencode/opencode.jsonc" "${HOME}/.config/opencode/opencode.jsonc"
cp "$SCRIPT_DIR/opencode/opencode-ask-prompt.txt" "${HOME}/.config/opencode/prompts/ask.txt"
cp "$SCRIPT_DIR/opencode/opencode-review-prompt.txt" "${HOME}/.config/opencode/prompts/review.txt"
