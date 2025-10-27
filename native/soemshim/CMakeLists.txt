cmake_minimum_required(VERSION 3.20)
project(soemshim C)

# Base path of this CMakeLists.txt
set(PROJECT_ROOT "${CMAKE_CURRENT_SOURCE_DIR}")

# SOEM root (folder that contains "include/soem/soem.h" and build-msvc/Release/soem.lib)
set(SOEM_SRC  "${PROJECT_ROOT}/../SOEM-2.0.0")
set(SOEM_LIB  "${SOEM_SRC}/lib/soem.lib")

# Npcap SDK unzipped root (folder that contains "include/pcap.h" and "Lib/wpcap.lib")
set(NPCAP_ROOT "${PROJECT_ROOT}/../Npcap-sdk-1.15/")

add_library(soemshim SHARED soem_shim.c)

# ---- Include paths (for headers) ----
# We point at the PARENT of the folder "soem", so #include <soem/soem.h> resolves.
target_include_directories(
	soemshim PRIVATE
  "${SOEM_SRC}/include"
  "${NPCAP_ROOT}/Include"
)

#---- Library search dir (for .lib files) ----
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
  set(NPCAP_LIBDIR "${NPCAP_ROOT}/Lib/x64")
else()
  set(NPCAP_LIBDIR "${NPCAP_ROOT}/Lib")
endif()
link_directories("${NPCAP_LIBDIR}")

# ---- Libraries to link ----
target_link_libraries(soemshim PRIVATE
  "${SOEM_LIB}"   # static SOEM you built with MSVC
  "${NPCAP_LIBDIR}/wpcap.lib"           # from Npcap SDK
  "${NPCAP_LIBDIR}/Packet.lib"          # from Npcap SDK
  ws2_32          # Windows sockets
  winmm           # Windows multimedia timing
)

# ---- Compiler tweaks ----
target_compile_definitions(soemshim PRIVATE _CRT_SECURE_NO_WARNINGS)