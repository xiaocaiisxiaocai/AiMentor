# syntax=docker/dockerfile:1.7

# 还原层只携带项目清单，使普通源码修改能够复用 NuGet 缓存。
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS restore
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/AiMentor.Api/AiMentor.Api.csproj src/AiMentor.Api/
COPY src/AiMentor.Application/AiMentor.Application.csproj src/AiMentor.Application/
COPY src/AiMentor.Domain/AiMentor.Domain.csproj src/AiMentor.Domain/
COPY src/AiMentor.Infrastructure/AiMentor.Infrastructure.csproj src/AiMentor.Infrastructure/
COPY src/AiMentor.Migrations/AiMentor.Migrations.csproj src/AiMentor.Migrations/
RUN dotnet restore src/AiMentor.Api/AiMentor.Api.csproj \
    && dotnet restore src/AiMentor.Migrations/AiMentor.Migrations.csproj

FROM restore AS publish
COPY src/ src/
RUN dotnet publish src/AiMentor.Api/AiMentor.Api.csproj \
        --configuration Release \
        --no-restore \
        --no-self-contained \
        --output /artifacts/api \
        /p:UseAppHost=false \
    && dotnet publish src/AiMentor.Migrations/AiMentor.Migrations.csproj \
        --configuration Release \
        --no-restore \
        --no-self-contained \
        --output /artifacts/migrations \
        /p:UseAppHost=false

# chiseled 运行时不包含 shell 与包管理器，缩小生产攻击面。
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:f9bd6be9b5ab75b8196bff0f0972580edaea7fa8ca04e6ef530950e33caee5b0 AS runtime
WORKDIR /app/api

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    AIMENTOR_KNOWLEDGE_ROOT=/app/api/wwwroot \
    AIMENTOR_MIGRATIONS_ROOT=/app/deploy/sql \
    Migrations__Root=/app/deploy/sql

EXPOSE 8080

# 最终镜像只接收发布输出与版本化迁移脚本，不携带源码、SDK 或测试制品。
COPY --from=publish --chown=1654:1654 /artifacts/api/ /app/api/
COPY --from=publish --chown=1654:1654 /artifacts/migrations/ /app/migrations/
COPY --chown=1654:1654 deploy/sql/ /app/deploy/sql/

USER 1654
ENTRYPOINT ["dotnet", "/app/api/AiMentor.Api.dll"]
