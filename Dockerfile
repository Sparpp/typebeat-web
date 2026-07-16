FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Typebeat.Web/Typebeat.Web.csproj -c Release -o /app

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
