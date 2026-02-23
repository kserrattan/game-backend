# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj + restore first (better caching)
COPY backend.csproj ./
RUN dotnet restore

# copy everything else + publish
COPY . ./
RUN dotnet publish -c Release -o /app/publish

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# Render sets PORT; ASP.NET must listen on it
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

CMD ["dotnet", "backend.dll"]
