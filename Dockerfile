# The web UI; only its static build reaches the image.
FROM node:26-trixie-slim AS ui
WORKDIR /ui
COPY ui/package.json ui/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci
COPY ui/ ./
RUN npm run build

# Native glibc build on the runtime's Ubuntu release.
FROM ubuntu:26.04 AS rust
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential ca-certificates cmake curl \
    && rm -rf /var/lib/apt/lists/*
ENV RUSTUP_HOME=/usr/local/rustup CARGO_HOME=/usr/local/cargo PATH=/usr/local/cargo/bin:$PATH
WORKDIR /src
COPY rust-toolchain.toml ./
RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs \
        | sh -s -- -y --profile minimal --default-toolchain none \
    && rustup toolchain install \
    && cargo install cargo-chef --locked

FROM rust AS plan
COPY . .
RUN cargo chef prepare --recipe-path recipe.json

# Dependencies build in their own layer, reused until Cargo.lock or a manifest changes.
FROM rust AS build
COPY --from=plan /src/recipe.json ./
# The recipe holds workspace members only; [patch] path crates need their real source.
COPY vendor/ vendor/
RUN cargo chef cook --release --locked -p infinidysk --recipe-path recipe.json
COPY . .
COPY --from=ui /ui/dist ui/dist
# The full commit hash, so the server can tell when `main` has moved on.
ARG COMMIT=""
RUN INFINIDYSK_COMMIT="$COMMIT" cargo build --release --locked -p infinidysk \
    && cp target/release/infinidysk /usr/local/bin/infinidysk

# Only the binary, for the release archives (`--target binary --output type=local,dest=<dir>`).
FROM scratch AS binary
COPY --from=build /usr/local/bin/infinidysk /infinidysk

FROM ubuntu:26.04
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg fuse3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /usr/local/bin/infinidysk /usr/local/bin/infinidysk
# Declared after the layers above so a new commit does not rebuild them.
ARG COMMIT=""
ARG VERSION=""
LABEL org.opencontainers.image.title="infinidysk" \
      org.opencontainers.image.description="Usenet-backed virtual filesystem with a SABnzbd-compatible API, FUSE and WebDAV" \
      org.opencontainers.image.source="https://github.com/infinidysk/infinidysk" \
      org.opencontainers.image.url="https://www.infinidysk.com/" \
      org.opencontainers.image.documentation="https://www.infinidysk.com/v2/" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.revision="$COMMIT" \
      org.opencontainers.image.version="$VERSION"
VOLUME /config
EXPOSE 3000
# Healthy once the database, the FUSE mount (when /dev/fuse is there) and the import worker
# answer. The start period covers a first start that imports a 1.x install; a normal start
# turns healthy within seconds.
HEALTHCHECK --interval=30s --timeout=10s --start-period=10m --start-interval=5s --retries=3 \
    CMD ["/usr/local/bin/infinidysk", "healthcheck"]
ENTRYPOINT ["/usr/local/bin/infinidysk"]
