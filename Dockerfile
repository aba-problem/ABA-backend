# ============================================================================
# Build multi-stage — la imagen final solo lleva el runtime ASP.NET (no el SDK),
# clave para el presupuesto de RAM del Módulo 6 (backend: 300-400MB en reposo).
# ============================================================================

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY abaproblem.csproj .
RUN dotnet restore abaproblem.csproj

COPY . .
RUN dotnet publish abaproblem.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Kestrel escucha en 8080 dentro del contenedor; solo Nginx lo alcanza vía la red
# interna de Docker (Módulo 5.3: el backend nunca se expone directo a internet).
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# Ejecuta como usuario no-root (imagen base ya trae "app").
USER app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "abaproblem.dll"]
