# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Portal.Api.csproj ./
RUN dotnet restore Portal.Api.csproj
COPY . ./
RUN dotnet publish Portal.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
# Vietnamese culture / ICU already available in aspnet image.
COPY --from=build /app/publish ./
# Data Protection keys persist here (mounted volume) so encrypted connection
# passwords stay decryptable across container restarts.
RUN mkdir -p /app/keys
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Portal.Api.dll"]
