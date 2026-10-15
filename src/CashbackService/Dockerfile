FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/CashbackService/CashbackService.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 7003
ENV ASPNETCORE_URLS=http://+:7003
ENTRYPOINT ["dotnet", "CashbackService.dll"]
