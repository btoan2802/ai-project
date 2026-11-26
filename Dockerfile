# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Prevent MSBuild server & Roslyn worker hang/deadlock in container
ENV DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
ENV MSBUILDDISABLENODEREUSE=1
ENV UseSharedCompilation=false
ENV DOTNET_NOLOGO=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Copy project file and restore dependencies
COPY ["BtoanAiNet.csproj", "./"]
RUN dotnet restore "BtoanAiNet.csproj"

# Copy all project files and build
COPY . .
RUN dotnet publish "BtoanAiNet.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime with .NET SDK for Web IDE terminal compilation & execution
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app

# Prevent MSBuild server & Roslyn worker hang/deadlock during runtime builds in container
ENV DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1
ENV MSBUILDDISABLENODEREUSE=1
ENV UseSharedCompilation=false
ENV DOTNET_NOLOGO=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Install bash, curl, git, procps, psmisc, and Node.js 22 LTS + @angular/cli
RUN apt-get update && apt-get install -y curl git procps psmisc && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    npm install -g @angular/cli && \
    rm -rf /var/lib/apt/lists/*

ENV NPM_CONFIG_CACHE=/tmp/.npm

COPY --from=build /app/publish .

# Pre-cache NuGet packages for template projects so runtime builds take 1-2s
RUN cd /app/WorkspaceTemplates/shop-senior-mvc && \
    dotnet restore --disable-parallel --nologo && \
    dotnet build --no-restore --nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false -maxCpuCount:1 && \
    rm -rf bin obj

# Persistent storage volume for user workspaces and SQLite databases
VOLUME ["/app/App_Data"]

# Expose ports (Render, Koyeb, HF default to 8080 or PORT env)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "BtoanAiNet.dll"]
