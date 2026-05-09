FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 27018

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MongoSpyglass.Service/MongoSpyglass.Service.csproj", "MongoSpyglass.Service/"]
COPY ["MongoSpyglass.Proxy/MongoSpyglass.Proxy.csproj", "MongoSpyglass.Proxy/"]
RUN dotnet restore "MongoSpyglass.Service/MongoSpyglass.Service.csproj"
COPY . .
WORKDIR "/src/MongoSpyglass.Service"
RUN dotnet build "MongoSpyglass.Service.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MongoSpyglass.Service.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MongoSpyglass.Service.dll"]
