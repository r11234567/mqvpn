# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors

# project(VERSION) and shared-library VERSION/SOVERSION remain numeric ABI
# metadata. Human-readable build identity is independent of that ABI version.
set(MQVPN_VERSION_SUFFIX "" CACHE STRING "Legacy suffix appended to the core version")
set(MQVPN_BUILD_VERSION "" CACHE STRING "Standalone snapshot build identity")
if(MQVPN_BUILD_VERSION)
    if(NOT MQVPN_BUILD_VERSION MATCHES "^snapshot-([0-9]+)-([0-9]+)-UTC$")
        message(FATAL_ERROR "Invalid snapshot identity: ${MQVPN_BUILD_VERSION}")
    endif()
    string(LENGTH "${CMAKE_MATCH_1}" date_length)
    string(LENGTH "${CMAKE_MATCH_2}" time_length)
    if(NOT date_length EQUAL 8 OR NOT time_length EQUAL 4)
        message(FATAL_ERROR "Invalid snapshot identity: ${MQVPN_BUILD_VERSION}")
    endif()
    if(MQVPN_VERSION_SUFFIX)
        message(FATAL_ERROR "A snapshot identity cannot also have a legacy version suffix")
    endif()
    string(REGEX REPLACE "^snapshot-([0-9][0-9][0-9][0-9])([0-9][0-9])([0-9][0-9])-([0-9][0-9])([0-9][0-9])-UTC$"
           "snapshot \\1-\\2-\\3 \\4:\\5 UTC" MQVPN_FULL_VERSION "${MQVPN_BUILD_VERSION}")
    # dpkg requires a leading digit. Keep snapshot ordering below a real
    # release without borrowing that release's checked-in version number.
    set(MQVPN_PACKAGE_VERSION "0~${MQVPN_BUILD_VERSION}")
    add_compile_definitions(MQVPN_BUILD_VERSION_STR="${MQVPN_FULL_VERSION}")
else()
    if(MQVPN_VERSION_SUFFIX AND NOT MQVPN_VERSION_SUFFIX MATCHES "^[-+~][A-Za-z0-9.+~-]+$")
        message(FATAL_ERROR "Invalid legacy MQVPN_VERSION_SUFFIX: ${MQVPN_VERSION_SUFFIX}")
    endif()
    set(MQVPN_FULL_VERSION "${PROJECT_VERSION}${MQVPN_VERSION_SUFFIX}")
    set(MQVPN_PACKAGE_VERSION "${MQVPN_FULL_VERSION}")
    if(MQVPN_VERSION_SUFFIX MATCHES "^-")
        string(REGEX REPLACE "^-" "~" package_suffix "${MQVPN_VERSION_SUFFIX}")
        set(MQVPN_PACKAGE_VERSION "${PROJECT_VERSION}${package_suffix}")
    endif()
    if(MQVPN_VERSION_SUFFIX)
        add_compile_definitions(MQVPN_VERSION_SUFFIX_STR="${MQVPN_VERSION_SUFFIX}")
    endif()
endif()
set(MQVPN_ARTIFACT_VERSION "${MQVPN_FULL_VERSION}")
if(MQVPN_BUILD_VERSION)
    set(MQVPN_ARTIFACT_VERSION "${MQVPN_BUILD_VERSION}")
endif()
message(STATUS "mqvpn build version: ${MQVPN_FULL_VERSION} (package ${MQVPN_PACKAGE_VERSION})")
