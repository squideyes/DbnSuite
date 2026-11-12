# DbnSync worker. Build from the repository root:
#   docker build -t dbnsync .

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY Aspire/ServiceDefaults/ Aspire/ServiceDefaults/
COPY Core/ Core/

RUN dotnet publish Core/DbnSync.Worker/DbnSync.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app

# Owned by the app user, so a named volume mounted here starts writable.
RUN mkdir /scratch && chown $APP_UID /scratch

COPY --from=build /app .

# The base image already listens on 8080.
ENV DbnSync__ScratchPath=/scratch

USER $APP_UID
EXPOSE 8080
VOLUME /scratch

ENTRYPOINT ["dotnet", "DbnSync.Worker.dll"]
