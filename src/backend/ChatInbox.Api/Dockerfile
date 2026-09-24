FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/backend/ ./src/backend/
RUN dotnet restore src/backend/ChatInbox.Api/ChatInbox.Api.csproj
RUN dotnet publish src/backend/ChatInbox.Api/ChatInbox.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChatInbox.Api.dll"]
