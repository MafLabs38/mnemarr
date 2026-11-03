# syntax=docker/dockerfile:1

FROM node:26-alpine AS frontend
WORKDIR /build
COPY package.json yarn.lock ./
RUN corepack enable && yarn install --frozen-lockfile
COPY frontend ./frontend
RUN yarn build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
ARG VERSION=0.0.0-dev
WORKDIR /build
COPY global.json Directory.Build.props Directory.Packages.props ./
# The projects that ship, copied alone first so that packages are restored in a layer a source change does not invalidate.
COPY src/Mnemarr.Host/Mnemarr.Host.csproj src/Mnemarr.Host/
COPY src/Mnemarr.Api.V1/Mnemarr.Api.V1.csproj src/Mnemarr.Api.V1/
COPY src/Mnemarr.Core/Mnemarr.Core.csproj src/Mnemarr.Core/
RUN dotnet restore src/Mnemarr.Host/Mnemarr.Host.csproj
COPY src ./src
RUN dotnet publish src/Mnemarr.Host/Mnemarr.Host.csproj -c Release --no-restore -o /app -p:Version=${VERSION} \
    && mkdir /config

# Chiseled image: no shell, no package manager, non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /build/_output/UI ./wwwroot
# The chiseled image has no shell to create folders: copy an empty one owned by the app user.
COPY --from=backend --chown=1654:1654 /config /config
ENV MNEMARR__DATAFOLDER=/config
VOLUME /config
EXPOSE 8383
ENTRYPOINT ["dotnet", "Mnemarr.dll"]
