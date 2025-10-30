# soemshim for lean Linux containers

This directory mirrors the `native/soemshim` sources but ships with a minimal
CMake build that targets glibc-based Docker containers. The goal is to be able
to build the shared library without the full Visual Studio toolchain while
running inside a lightweight image (e.g. `debian:bookworm-slim`).

## Dependencies

* **SOEM** headers and libraries. Either install the `libsoem-dev` package from
  your distribution or build SOEM from source and point `SOEM_ROOT` to the
  install prefix.
* **libpcap** (`libpcap-dev`) – the Linux equivalent of Npcap. SOEM relies on
  raw Ethernet access so packet capture support is required.
* A C toolchain with CMake 3.16+.

## Building on Linux

```bash
# inside the repository root
cd native/soemshim-linux
cmake -B build -DSOEM_ROOT=/opt/soem
cmake --build build --config Release
sudo cmake --install build --prefix /usr/local
```

If SOEM is installed globally you can omit `-DSOEM_ROOT=...` and rely on the
system's pkg-config paths.

## Docker example

```Dockerfile
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential cmake libpcap-dev git curl && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN cmake -B native/soemshim-linux/build -DSOEM_ROOT=/opt/soem \
    && cmake --build native/soemshim-linux/build --config Release
```

Mount the resulting `libsoemshim.so` next to the managed services or into a
shared `/usr/local/lib` volume. The build artifacts are ABI-compatible with the
existing .NET interop layer.
