FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

# Microsoft feed
RUN wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb

# .NET 8 SDK
RUN sudo apt-get update && \
    sudo apt-get install -y dotnet-sdk-8.0 && \
    sudo apt-get clean && \
    rm -rf /var/lib/apt/lists/*
