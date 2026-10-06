#!/bin/bash
set -e

echo "Restoring dotnet tools..."
dotnet tool restore

export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:$SSL_CERT_DIR"
echo "Trusting ASP.NET Core HTTPS development certificate..."
dotnet dev-certs https --trust
