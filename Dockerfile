# syntax=docker/dockerfile:1.4

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend-build

WORKDIR /frontend

COPY ./frontend/package.json ./frontend/package-lock.json ./
RUN npm ci
COPY ./frontend ./
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build

WORKDIR /src

# Accept build-time architecture as ARG (e.g., x64 or arm64)
ARG TARGETARCH
COPY ./backend/NzbWebDAV.csproj ./backend/nuget.config ./backend/
COPY ./libs/SharpCompress/SharpCompress.csproj ./libs/SharpCompress/
RUN dotnet restore backend/NzbWebDAV.csproj -r linux-musl-${TARGETARCH}
COPY ./backend ./backend
COPY ./libs ./libs
RUN dotnet publish backend/NzbWebDAV.csproj -c Release -r linux-musl-${TARGETARCH} -o ./backend/publish --no-restore

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# Label the image
ARG REPO_URL
LABEL org.opencontainers.image.source=${REPO_URL}

# Prepare environment
WORKDIR /app
RUN mkdir /config \
    && apk add --no-cache nodejs npm libc6-compat shadow su-exec bash curl tzdata

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/dist-node/server ./frontend/dist-node/server
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /src/backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Set env variables
EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ARG NZBDAV_COMMIT_SHA
ENV NZBDAV_COMMIT_SHA=${NZBDAV_COMMIT_SHA}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

CMD ["/entrypoint.sh"]
