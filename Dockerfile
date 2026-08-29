# Peanut Gallery as a Docker container GitHub Action.
# Stage 1 builds the CLI; stage 2 is a slim runtime image whose entrypoint runs
# `review-pr`. Used by action.yml (runs.using: docker).
#
# The restore layer is split from the source copy: copying only the project graph +
# build config first lets `dotnet restore` cache across source-only edits, so the
# buildkit registry cache (.github/workflows/image.yml) actually pays off instead of
# being busted by a single-line change to the source.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Project graph + build config only -> this layer (and the restore below) stays
#    cached as long as no csproj / props / nuget.config / global.json changes.
COPY global.json PeanutGallery.slnx Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/PeanutGallery.Cli/PeanutGallery.Cli.csproj src/PeanutGallery.Cli/
COPY src/PeanutGallery.Core/PeanutGallery.Core.csproj src/PeanutGallery.Core/
COPY src/PeanutGallery.Engine/PeanutGallery.Engine.csproj src/PeanutGallery.Engine/
RUN dotnet restore src/PeanutGallery.Cli/PeanutGallery.Cli.csproj

# 2) Now the sources; publish without re-restoring.
# UseAppHost=false: no native launcher needed, we invoke via `dotnet`.
COPY . .
RUN dotnet publish src/PeanutGallery.Cli/PeanutGallery.Cli.csproj -c Release -o /app \
      /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
LABEL org.opencontainers.image.source="https://github.com/charles8051/peanut-gallery" \
      org.opencontainers.image.description="Persona-driven, multi-model PR review" \
      org.opencontainers.image.licenses="MIT"
COPY --from=build /app /app
COPY action/default.json /peanut/default.json
COPY action/entrypoint.sh /usr/local/bin/peanut-entrypoint
RUN chmod +x /usr/local/bin/peanut-entrypoint
ENTRYPOINT ["peanut-entrypoint"]
