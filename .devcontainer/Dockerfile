# Copied from https://github.com/devcontainers/images/blob/main/src/dotnet/.devcontainer/Dockerfile
# so that we can control the SDK version and add additional dependencies for our .NET development environment.
ARG VARIANT=10.0-resolute-aot
FROM mcr.microsoft.com/dotnet/sdk:${VARIANT}

RUN chmod -R a+w /usr/share/dotnet && userdel -f -r ubuntu

# clear this environment variable so xml docs from NuGet packages are unpackaged. The default dotnet/sdk image sets it to 'skip'.
# see https://github.com/dotnet/dotnet-docker/issues/2790
ENV NUGET_XMLDOC_MODE=

# Install additional dependencies for cross-compilation to ARM architectures.
RUN dpkg --add-architecture arm64 && \
    dpkg --add-architecture armhf && \
    sed -i -e 's/Types: deb/Types: deb\nArchitectures: amd64/g' /etc/apt/sources.list.d/ubuntu.sources && \
    printf 'Types: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports/\nSuites: resolute resolute-updates\nComponents: main restricted\nArchitectures: arm64 armhf\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\nEnabled: yes\n\nTypes: deb\nURIs: http://ports.ubuntu.com/ubuntu-ports/\nSuites: resolute-backports\nComponents: main restricted universe multiverse\nArchitectures: arm64 armhf\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\nEnabled: yes\n' > /etc/apt/sources.list.d/ports.sources && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        binutils-aarch64-linux-gnu \
        binutils-arm-linux-gnueabihf \
        gcc-aarch64-linux-gnu \
        gcc-arm-linux-gnueabihf \
        zlib1g-dev:arm64 \
        zlib1g-dev:armhf \
        zlib1g-dev \
        clang \
        llvm && \
    rm -rf /var/lib/apt/lists/*

# Install additional tools
# These are not strictly necessary for .NET development, but they are commonly used tools that can be helpful to have in the container.
# ripgrep is a fast search tool that can be used to search for text in files. It is often used as a replacement for grep.
# fdclone is a fast and user-friendly alternative to the find command. It is often used to quickly find files in a directory tree.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ripgrep \
        fdclone \
        && \
    rm -rf /var/lib/apt/lists/*
