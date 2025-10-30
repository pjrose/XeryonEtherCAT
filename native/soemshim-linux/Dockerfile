# syntax=docker/dockerfile:1

# Builder stage: compile SOEM and the soemshim wrapper
FROM debian:bookworm-slim AS builder

ARG SOEM_VERSION=v1.4.0
ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        cmake \
        git \
        libpcap-dev \
    && rm -rf /var/lib/apt/lists/*

# Fetch and build SOEM
WORKDIR /tmp/soem
RUN git clone --depth 1 --branch "${SOEM_VERSION}" https://github.com/OpenEtherCATsociety/SOEM.git .
RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=/opt/soem \
    && cmake --build build --config Release \
    && cmake --install build --config Release

# Build the shim using the freshly built SOEM artifacts
WORKDIR /src
COPY . .
RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DSOEM_ROOT=/opt/soem \
    && cmake --build build --config Release \
    && cmake --install build --prefix /opt/soemshim \
    && mkdir -p /artifacts \
    && cp /opt/soemshim/lib/libsoemshim.so /artifacts/

# Runtime image carries the compiled libraries ready for consumption
FROM debian:bookworm-slim AS runtime
ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libpcap0.8 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /opt/soem /opt/soem
COPY --from=builder /opt/soemshim /opt/soemshim
COPY --from=builder /artifacts /artifacts

ENV SOEM_ROOT=/opt/soem \
    SOEMSHIM_ROOT=/opt/soemshim \
    LD_LIBRARY_PATH=/opt/soem/lib:/opt/soemshim/lib

WORKDIR /workspace

CMD ["/bin/bash"]
