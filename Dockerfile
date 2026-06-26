FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore ChainOfRepair.sln
RUN dotnet publish src/ChainOfRepair.Web/ChainOfRepair.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChainOfRepair.Web.dll"]
