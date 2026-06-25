FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8093
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/CrmAi.Domain/CrmAi.Domain.csproj", "src/CrmAi.Domain/"]
COPY ["src/CrmAi.Application/CrmAi.Application.csproj", "src/CrmAi.Application/"]
COPY ["src/CrmAi.Infrastructure/CrmAi.Infrastructure.csproj", "src/CrmAi.Infrastructure/"]
COPY ["src/CrmAi.Api/CrmAi.Api.csproj", "src/CrmAi.Api/"]
RUN dotnet restore "src/CrmAi.Api/CrmAi.Api.csproj"
COPY . .
RUN dotnet publish "src/CrmAi.Api/CrmAi.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CrmAi.Api.dll"]
