# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app
RUN apk add --upgrade --no-cache tzdata \
    && mkdir -p /app/Data /app/StoredObjects /app/Logs /app/MatterCredentials \
    && chown -R $APP_UID:0 /app
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

# This stage is used to build the service project.
# NOTE: the Debian-based SDK image is required (not -alpine): Grpc.Tools ships a glibc-linked
# protoc, which cannot be executed on musl/Alpine. The publish output is portable (no RID,
# UseAppHost=false), so it still runs on the Alpine runtime image used by the final stage.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG NUGET_AUTH_TOKEN
ARG NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json
WORKDIR /src
COPY ["RIoT2.Net.Orchestrator.csproj", "."]
RUN test -n "$NUGET_AUTH_TOKEN" && dotnet nuget add source -n github -u AZ -p $NUGET_AUTH_TOKEN --store-password-in-clear-text $NUGET_URL
RUN dotnet restore "./RIoT2.Net.Orchestrator.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./RIoT2.Net.Orchestrator.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RIoT2.Net.Orchestrator.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
RUN mkdir -p /app/Data
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RIoT2.Net.Orchestrator.dll"]

#Set default environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV TZ=Europe/Helsinki