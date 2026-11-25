# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["BtoanAiNet.csproj", "./"]
RUN dotnet restore "BtoanAiNet.csproj"

# Copy all project files and build
COPY . .
RUN dotnet publish "BtoanAiNet.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime with .NET SDK for Web IDE terminal compilation & execution
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
WORKDIR /app

# Install bash, curl, git, procps, psmisc, and Node.js 22 LTS + @angular/cli
RUN apt-get update && apt-get install -y curl git procps psmisc && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    npm install -g @angular/cli && \
    rm -rf /var/lib/apt/lists/*

ENV NPM_CONFIG_CACHE=/tmp/.npm

COPY --from=build /app/publish .

# Persistent storage volume for user workspaces and SQLite databases
VOLUME ["/app/App_Data"]

# Expose ports (Render, Koyeb, HF default to 8080 or PORT env)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "BtoanAiNet.dll"]
