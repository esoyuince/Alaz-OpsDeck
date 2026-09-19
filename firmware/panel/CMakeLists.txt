cmake_minimum_required(VERSION 3.16)

option(OPSDECK_TAILSCALE "Build optional direct Tailscale transport via MicroLink" OFF)
if(OPSDECK_TAILSCALE)
    # Keep the optional transport reproducible without changing the stable LAN-only sdkconfig.
    set(SDKCONFIG "${CMAKE_CURRENT_LIST_DIR}/sdkconfig.tailscale")
    set(SDKCONFIG_DEFAULTS "${CMAKE_CURRENT_LIST_DIR}/sdkconfig.defaults;${CMAKE_CURRENT_LIST_DIR}/sdkconfig.tailscale.defaults")
    set(OPSDECK_MICROLINK_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../third_party/microlink")
    if(NOT EXISTS "${OPSDECK_MICROLINK_ROOT}/components/microlink/CMakeLists.txt")
        message(FATAL_ERROR "MicroLink submodule missing. Run: git submodule update --init --recursive")
    endif()
    # Upstream components/wireguard_lwip is a git symlink. Explicitly use the
    # nested component so Windows checkouts are deterministic as well.
    list(APPEND EXTRA_COMPONENT_DIRS
        "${OPSDECK_MICROLINK_ROOT}/components/microlink"
        "${OPSDECK_MICROLINK_ROOT}/components/microlink/components/wireguard_lwip")
    add_compile_definitions(OPSDECK_TAILSCALE=1)
else()
    add_compile_definitions(OPSDECK_TAILSCALE=0)
endif()

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(opsdeck_panel)

if(OPSDECK_TAILSCALE)
    # GCC 15 reports bounded strncpy diagnostics in the pinned third-party
    # component. Keep them as warnings without weakening OpsDeck -Werror.
    idf_component_get_property(OPSDECK_MICROLINK_LIB microlink COMPONENT_LIB)
    target_compile_options(${OPSDECK_MICROLINK_LIB} PRIVATE -Wno-error=stringop-truncation)
endif()
