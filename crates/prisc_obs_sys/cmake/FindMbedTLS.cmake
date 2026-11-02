# prisc_recorder's FindMbedTLS (OBS 33 dropped cmake/finders/FindMbedTLS.cmake and
# expects Mbed TLS's own CMake package config, which a source-built static
# archive merged into one library does not provide).
#
# On Linux prisc_obs_sys links a static Mbed TLS built from source (see
# linux_ensure_mbedtls in crates/prisc_obs_sys/build.rs) as one merged archive
# (tls + x509 + crypto), handed in through Mbedtls_LIBRARY / MbedTLS_INCLUDE_DIR.
# This defines the one target OBS links, MbedTLS::mbedtls, from those.
include(FindPackageHandleStandardArgs)

find_path(MbedTLS_INCLUDE_DIR NAMES mbedtls/ssl.h)
find_library(Mbedtls_LIBRARY NAMES mbedtls-combined mbedtls)

if(MbedTLS_INCLUDE_DIR AND EXISTS "${MbedTLS_INCLUDE_DIR}/mbedtls/build_info.h")
  file(STRINGS "${MbedTLS_INCLUDE_DIR}/mbedtls/build_info.h" _mbedtls_version_line REGEX "^#define MBEDTLS_VERSION_STRING[ \t]+\"")
  string(REGEX REPLACE ".*\"([0-9.]+)\".*" "\\1" MbedTLS_VERSION "${_mbedtls_version_line}")
endif()

find_package_handle_standard_args(
  MbedTLS
  REQUIRED_VARS Mbedtls_LIBRARY MbedTLS_INCLUDE_DIR
  VERSION_VAR MbedTLS_VERSION
)

if(MbedTLS_FOUND AND NOT TARGET MbedTLS::mbedtls)
  add_library(MbedTLS::mbedtls UNKNOWN IMPORTED)
  set_target_properties(
    MbedTLS::mbedtls
    PROPERTIES IMPORTED_LOCATION "${Mbedtls_LIBRARY}" INTERFACE_INCLUDE_DIRECTORIES "${MbedTLS_INCLUDE_DIR}"
  )
endif()

mark_as_advanced(Mbedtls_LIBRARY MbedTLS_INCLUDE_DIR)
