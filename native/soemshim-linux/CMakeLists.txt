cmake_minimum_required(VERSION 3.16)
project(soemshim_linux C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

option(SOEM_ROOT "Root of the SOEM installation" "")

if (SOEM_ROOT)
    set(SOEM_HINTS ${SOEM_ROOT}/lib ${SOEM_ROOT}/lib64)
    set(SOEM_INCLUDE_HINTS ${SOEM_ROOT}/include)
endif()

find_library(SOEM_LIBRARY NAMES soem HINTS ${SOEM_HINTS})
find_path(SOEM_INCLUDE_DIR soem/soem.h HINTS ${SOEM_INCLUDE_HINTS})

if (NOT SOEM_LIBRARY OR NOT SOEM_INCLUDE_DIR)
    message(FATAL_ERROR "SOEM not found. Provide SOEM_ROOT or install the library via your package manager.")
endif()

find_library(PCAP_LIBRARY NAMES pcap REQUIRED)

add_library(soemshim SHARED soem_shim.c)
target_include_directories(soemshim PRIVATE ${SOEM_INCLUDE_DIR})
target_link_libraries(soemshim PRIVATE ${SOEM_LIBRARY} ${PCAP_LIBRARY})

install(TARGETS soemshim DESTINATION lib)
install(FILES soem_shim.h DESTINATION include)
