FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# nuget.config declares the external/packages local feed (used only by the wire-compat test
# job to serve the game's resources package). The folder is excluded from this build context
# (.dockerignore) and from deploy ships — but NuGet hard-fails restore if a declared local
# source is missing, so materialize it empty.
RUN mkdir -p external/packages \
    && dotnet publish src/Typebeat.Web/Typebeat.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Npgsql probes for Kerberos support at startup; without the lib it logs a loud (harmless)
# error on every boot. We use password auth, but clean logs are worth one small package.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Typebeat.Web.dll"]
