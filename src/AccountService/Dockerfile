FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AccountService/AccountService.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 7001
ENV ASPNETCORE_URLS=http://+:7001
ENTRYPOINT ["dotnet", "AccountService.dll"]
