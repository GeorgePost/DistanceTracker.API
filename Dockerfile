
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /app

# Restore dependencies (cached layer)
COPY ["DistanceTracker.API.csproj", "./"]
RUN dotnet restore "DistanceTracker.API.csproj"

# Copy source code and build
COPY . .
RUN dotnet build "DistanceTracker.API.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "DistanceTracker.API.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
EXPOSE 8080

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DistanceTracker.API.dll"]
