# syntax=docker/dockerfile:1
# Build multi-stage: SDK só no build; a imagem final tem apenas o runtime ASP.NET.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restaura primeiro só com os csproj: a camada de pacotes fica em cache
# enquanto as dependências não mudarem.
COPY global.json Directory.Build.props Directory.Packages.props Floruna.slnx ./
COPY src/Floruna.SharedKernel/*.csproj src/Floruna.SharedKernel/
COPY src/Floruna.Domain/*.csproj src/Floruna.Domain/
COPY src/Floruna.Application/*.csproj src/Floruna.Application/
COPY src/Floruna.Infrastructure/*.csproj src/Floruna.Infrastructure/
COPY src/Floruna.Api/*.csproj src/Floruna.Api/
COPY src/Floruna.Migration/*.csproj src/Floruna.Migration/
RUN dotnet restore src/Floruna.Api/Floruna.Api.csproj && dotnet restore src/Floruna.Migration/Floruna.Migration.csproj
COPY src/ src/
RUN dotnet publish src/Floruna.Api/Floruna.Api.csproj -c Release -o /app --no-restore
# O ETL do Nest vai junto, em pasta própria: roda no servidor com a MESMA imagem (docs/migracao.md).
RUN dotnet publish src/Floruna.Migration/Floruna.Migration.csproj -c Release -o /app/migration --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ARG GIT_SHA=dev
ENV GIT_SHA=$GIT_SHA \
    # Mesma porta da API NestJS: o BFF continua com API_URL=http://api:4000
    ASPNETCORE_HTTP_PORTS=4000 \
    DOTNET_gcServer=1
EXPOSE 4000
COPY --from=build /app .
# Usuário sem privilégios (já existe na imagem oficial).
USER $APP_UID
ENTRYPOINT ["dotnet", "Floruna.Api.dll"]
