# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# The raw version pieces (design §3.9): the Docker build context excludes .git, so the
# release tag and commit hash cannot be read here — build with:
#   --build-arg BOT_VERSION_TAG=v1.2.3 --build-arg BOT_VERSION_COMMIT=1639324
# (deploy.sh exports both; without them the bot falls back to 0.0.0+unknown, FR-76).
ARG BOT_VERSION_TAG
ARG BOT_VERSION_COMMIT
WORKDIR /src
COPY ["HambotV2/HambotV2.csproj", "HambotV2/"]
RUN dotnet restore "./HambotV2/HambotV2.csproj"
COPY . .
WORKDIR "/src/HambotV2"
RUN dotnet build "./HambotV2.csproj" -c $BUILD_CONFIGURATION -o /app/build -p:BotVersionTag="$BOT_VERSION_TAG" -p:BotVersionCommit="$BOT_VERSION_COMMIT"

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG BOT_VERSION_TAG
ARG BOT_VERSION_COMMIT
RUN dotnet publish "./HambotV2.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false -p:BotVersionTag="$BOT_VERSION_TAG" -p:BotVersionCommit="$BOT_VERSION_COMMIT"

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HambotV2.dll"]