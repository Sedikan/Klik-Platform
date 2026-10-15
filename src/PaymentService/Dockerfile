FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PaymentService/PaymentService.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 7002
ENV ASPNETCORE_URLS=http://+:7002
ENTRYPOINT ["dotnet", "PaymentService.dll"]
