FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5010

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["tgSchoolBot/tgSchoolBot.csproj", "tgSchoolBot/"]
RUN dotnet restore "tgSchoolBot/tgSchoolBot.csproj"
COPY tgSchoolBot/ tgSchoolBot/
WORKDIR "/src/tgSchoolBot"
RUN dotnet build "./tgSchoolBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/tgSchoolBot"
RUN dotnet publish "./tgSchoolBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "tgSchoolBot.dll"]
