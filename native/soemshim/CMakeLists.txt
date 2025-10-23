cmake_minimum_required(VERSION 3.18)
project(soemshim C)

add_library(soemshim SHARED soem_shim.c)

if(DEFINED ENV{SOEM_ROOT})
    target_include_directories(soemshim PRIVATE $ENV{SOEM_ROOT}/include)
endif()

find_path(SOEM_INCLUDE_DIR soem/soem.h PATH_SUFFIXES soem PATHS /usr/include /usr/local/include)
if (SOEM_INCLUDE_DIR)
    target_include_directories(soemshim PRIVATE ${SOEM_INCLUDE_DIR})
endif()

find_library(SOEM_LIBRARY soem PATHS /usr/lib /usr/local/lib /usr/lib64 /usr/local/lib64)
if (SOEM_LIBRARY)
    target_link_libraries(soemshim PRIVATE ${SOEM_LIBRARY})
endif()

if (MSVC)
    target_compile_definitions(soemshim PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()
