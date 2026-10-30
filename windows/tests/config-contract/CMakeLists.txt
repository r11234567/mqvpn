cmake_minimum_required(VERSION 3.16)
project(mqvpn_gui_config_contract C)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
get_filename_component(MQVPN_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)
add_executable(config_contract config_contract.c
    ${MQVPN_ROOT}/src/config.c
    ${MQVPN_ROOT}/src/platform/client_config_bridge.c
    ${MQVPN_ROOT}/src/mqvpn_config.c
    ${MQVPN_ROOT}/src/redundant_tx.c
    ${MQVPN_ROOT}/src/log.c
    ${MQVPN_ROOT}/third_party/yyjson/yyjson.c)
target_include_directories(config_contract PRIVATE ${MQVPN_ROOT}/src ${MQVPN_ROOT}/include ${MQVPN_ROOT}/third_party/yyjson)
if(MSVC)
    target_compile_options(config_contract PRIVATE /utf-8 /W3)
    target_compile_definitions(config_contract PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_link_libraries(config_contract PRIVATE ws2_32)
else()
    target_compile_options(config_contract PRIVATE -Wall -Wextra -Werror)
    option(ENABLE_SANITIZERS "ASan and UBSan" OFF)
    if(ENABLE_SANITIZERS)
        target_compile_options(config_contract PRIVATE -fsanitize=address,undefined -fno-omit-frame-pointer)
        target_link_options(config_contract PRIVATE -fsanitize=address,undefined)
    endif()
endif()
