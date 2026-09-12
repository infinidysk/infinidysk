# syntax=docker/dockerfile:1.4

ARG ALPINE_VERSION=3.21

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend-build

WORKDIR /frontend

# NZBDAV_URL_BASE bakes the reverse-proxy sub-path (e.g. "/nzbdav") into the
# React Router basename, Vite asset paths, and the client bundle. Leave unset
# for root hosting (default, identical output to before this arg existed).
# Namespaced to avoid picking up a stray URL_BASE from shared build
# environments; the un-prefixed name is still honored as a fallback by the
# build config. See docs/configuration/url-base.md.
ARG NZBDAV_URL_BASE=""
ENV NZBDAV_URL_BASE=${NZBDAV_URL_BASE}

COPY ./frontend/package.json ./frontend/package-lock.json ./
RUN npm ci
COPY ./contracts/openapi/admin-v1.json /contracts/openapi/admin-v1.json
ENV ADMIN_OPENAPI_CONTRACT=/contracts/openapi/admin-v1.json
COPY ./frontend ./
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2a: Build rapidyenc musl native for the target arch --------
# Built on the target platform so linux-musl-* consumers (Alpine .NET images)
# get a real musl binary rather than a glibc fallback via the RID graph.
FROM alpine:${ALPINE_VERSION} AS rapidyenc-musl
RUN apk add --no-cache build-base cmake ninja
WORKDIR /src
COPY ./libs/rapidyenc/ ./
# Build into a directory outside the copied sources: a host-built librapidyenc.so
# that rode in with the context would otherwise be a candidate for the glob
# below, and `find` does not promise which match comes first.
#
# The linkage check is the load-bearing part. Alpine ships gcompat, so a
# glibc-linked native still loads here and only fails later, at the first call
# into a glibc-only symbol such as __memcpy_chk — a SIGSEGV in the middle of
# article decoding, far from anything that looks like a packaging problem.
RUN cmake -B /build -S /src -G Ninja -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /build --config Release --target rapidyenc_shared \
    && mkdir -p /out \
    && lib_path="$(find /build -name 'librapidyenc.so' -type f | head -n 1)" \
    && test -n "$lib_path" \
    && if readelf -dW "$lib_path" | grep -q 'Shared library: \[libc\.so\.6\]'; then \
        echo "ERROR: $lib_path is linked against glibc; expected a musl build." >&2; \
        readelf -dW "$lib_path" | grep NEEDED >&2; \
        exit 1; \
    fi \
    && cp "$lib_path" /out/librapidyenc.so

# -------- Stage 2c: Fetch the rclone binary for the target arch --------
# Runs on the build platform (it only downloads). The version is pinned so that
# rebuilding an image ships the rclone that was reviewed rather than whatever is
# newest that day; bump it deliberately. The script verifies the release's
# SHA256SUMS against rclone's signing key before trusting any checksum in it.
# Alpine's own rclone package is too old: it ships 1.68.2, while --links needs
# 1.70.3+.
FROM --platform=$BUILDPLATFORM alpine:${ALPINE_VERSION} AS rclone-fetch
RUN apk add --no-cache curl unzip gnupg
ARG TARGETARCH
ARG RCLONE_VERSION="v1.75.1"
COPY scripts/install-rclone.sh /install-rclone.sh
RUN TARGETARCH="${TARGETARCH}" RCLONE_VERSION="${RCLONE_VERSION}" DEST=/out sh /install-rclone.sh

# -------- Stage 2b: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build

WORKDIR /src

# Docker sets TARGETARCH to amd64/arm64; .NET runtime identifiers spell the
# same architectures x64/arm64. Concatenating the Docker spelling produced
# "linux-musl-amd64", which no package publishes assets for, so RID-specific
# asset selection had nothing to match on and the publish output carried a
# runtimes/ directory named after a RID that does not exist. Resolve the real
# RID once and use it everywhere below, and reject an arch we cannot map
# rather than inventing another name that silently matches nothing.
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
        amd64|x64) echo linux-musl-x64 > /rid ;; \
        arm64|aarch64) echo linux-musl-arm64 > /rid ;; \
        *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac
COPY ./Directory.Build.props ./Directory.Packages.props ./nuget.config ./.editorconfig ./
COPY ./backend/NzbWebDAV.csproj ./backend/
COPY ./libs/SharpCompress/SharpCompress.csproj ./libs/SharpCompress/
COPY ./libs/UsenetSharp/UsenetSharp.csproj ./libs/UsenetSharp/
COPY ./libs/RapidYencSharp/RapidYencSharp.csproj ./libs/RapidYencSharp/
RUN dotnet restore backend/NzbWebDAV.csproj -r "$(cat /rid)"

# Keep library compilation independent from backend-only source changes.
COPY ./libs/SharpCompress ./libs/SharpCompress
COPY ./libs/UsenetSharp ./libs/UsenetSharp
COPY ./libs/RapidYencSharp ./libs/RapidYencSharp
COPY ./libs/SharpCompress.snk ./libs/SharpCompress.snk

# Place the musl native where RapidYencSharp copies runtimes into the publish
# output. The directory has to exist and be non-empty before the build runs:
# RapidYencSharp's EnsureRapidYencNative target fires when the
# runtimes/linux-musl-*/native glob is empty and would otherwise fetch a
# published asset over the network.
COPY --from=rapidyenc-musl /out/librapidyenc.so /native/librapidyenc.so
RUN mkdir -p "libs/RapidYencSharp/runtimes/$(cat /rid)/native" \
    && cp /native/librapidyenc.so "libs/RapidYencSharp/runtimes/$(cat /rid)/native/librapidyenc.so"

RUN dotnet build libs/SharpCompress/SharpCompress.csproj -c Release -r "$(cat /rid)" --no-restore \
        -p:RunAnalyzers=false -p:EnforceCodeStyleInBuild=false \
    && dotnet build libs/UsenetSharp/UsenetSharp.csproj -c Release -r "$(cat /rid)" --no-restore \
        -p:RunAnalyzers=false -p:EnforceCodeStyleInBuild=false

COPY ./backend ./backend

RUN dotnet publish backend/NzbWebDAV.csproj -c Release -r "$(cat /rid)" -o ./backend/publish --no-restore \
        -p:RunAnalyzers=false -p:EnforceCodeStyleInBuild=false \
    && cp /native/librapidyenc.so ./backend/publish/librapidyenc.so

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# Label the image
ARG REPO_URL
LABEL org.opencontainers.image.source=${REPO_URL}
LABEL org.opencontainers.image.licenses=MIT

# Prepare environment
WORKDIR /app
RUN mkdir /config \
    && apk add --no-cache nodejs npm libc6-compat shadow su-exec bash curl tzdata fuse3

# rclone for the built-in mount feature, plus the FUSE setting that lets the
# non-root runtime user pass --allow-other so other containers and host users
# can read the mount. Mounting still requires the container to be started with
# --device /dev/fuse and --cap-add SYS_ADMIN.
COPY --from=rclone-fetch /out/rclone /usr/local/bin/rclone
RUN touch /etc/fuse.conf \
    && if [ -s /etc/fuse.conf ] && [ "$(tail -c1 /etc/fuse.conf | wc -l)" -eq 0 ]; then \
        printf '\n' >> /etc/fuse.conf; \
    fi \
    && if ! grep -qxF 'user_allow_other' /etc/fuse.conf; then \
        printf 'user_allow_other\n' >> /etc/fuse.conf; \
    fi

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/dist-node/server ./frontend/dist-node/server
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /src/backend/publish ./backend

# Entry and runtime setup
COPY scripts/preflight-config-path.sh /preflight-config-path.sh
COPY scripts/repair-config-path-ownership.sh /repair-config-path-ownership.sh
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh /preflight-config-path.sh /repair-config-path-ownership.sh

# Set env variables
EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ARG NZBDAV_COMMIT_SHA
ENV NZBDAV_COMMIT_SHA=${NZBDAV_COMMIT_SHA}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning
# Default the runtime half of NZBDAV_URL_BASE to the baked build-time value so
# a `--build-arg NZBDAV_URL_BASE=/x` image works without repeating the env var
# at run time. Overriding it with a different value fails fast at startup.
ARG NZBDAV_URL_BASE=""
ENV NZBDAV_URL_BASE=${NZBDAV_URL_BASE}

CMD ["/entrypoint.sh"]
