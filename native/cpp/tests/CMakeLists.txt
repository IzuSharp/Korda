set(NW_TEST_OUTPUT "${CMAKE_BINARY_DIR}/tests")
set(NW_FIXTURE_OUTPUT "${CMAKE_BINARY_DIR}/fixtures")

add_library(nw_interop_support STATIC
  "${PROJECT_SOURCE_DIR}/native/cpp/src/contracts.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/interop.cpp")
target_link_libraries(nw_interop_support PUBLIC nw_project_options)

function(nw_fixture name source)
  add_library(${name} SHARED "fixtures/${source}")
  target_link_libraries(${name} PRIVATE nw_interop_support)
  set_target_properties(${name} PROPERTIES LIBRARY_OUTPUT_DIRECTORY "${NW_FIXTURE_OUTPUT}" RUNTIME_OUTPUT_DIRECTORY "${NW_FIXTURE_OUTPUT}")
  if(UNIX AND NOT APPLE)
    target_link_options(${name} PRIVATE "LINKER:--exclude-libs,ALL"
      "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/fixtures/exports.map")
    if(NOT NW_SANITIZERS)
      target_link_options(${name} PRIVATE "LINKER:--no-undefined")
    endif()
  endif()
endfunction()

if(NW_INTEROP_ONLY)
  include(interop.cmake)
  return()
endif()

if(NW_BUILD_SQLITE)
  nw_fixture(korda_sqlite_fixture sqlite.cpp)
  target_sources(korda_sqlite_fixture PRIVATE
    "${PROJECT_SOURCE_DIR}/native/cpp/src/query.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/mutations.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/scalar.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/provider.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/host.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/administration.cpp")
  target_compile_definitions(korda_sqlite_fixture PRIVATE NW_HOST_TEST_HOOKS=1 NW_ENABLE_SQLITE=1 NW_ENABLE_POSTGRES=0)
  target_link_libraries(korda_sqlite_fixture PRIVATE nw_sqlite_engine)
  add_executable(nw_sqlite_fixture_tests sqlite_fixture_tests.cpp)
  target_link_libraries(nw_sqlite_fixture_tests PRIVATE nw_project_options korda_sqlite_fixture)
  set_target_properties(nw_sqlite_fixture_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
  add_test(NAME native.sqlite14 COMMAND nw_sqlite_fixture_tests)
  set_tests_properties(native.sqlite14 PROPERTIES TIMEOUT 120 LABELS "native;contract;provider")
endif()
if(NW_BUILD_POSTGRESQL)
  add_library(nw_host_test_support STATIC
    "${PROJECT_SOURCE_DIR}/native/cpp/src/contracts.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/interop.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/query.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/mutations.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/scalar.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/provider.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/host.cpp"
    "${PROJECT_SOURCE_DIR}/native/cpp/src/administration.cpp")
  target_link_libraries(nw_host_test_support PUBLIC nw_project_options
    "${NW_LIBPQ_STATIC}" "${NW_PGCOMMON_SHARED_STATIC}" "${NW_PGPORT_SHARED_STATIC}"
    OpenSSL::SSL ZLIB::ZLIB ${CMAKE_DL_LIBS})
  target_include_directories(nw_host_test_support SYSTEM PRIVATE ${PostgreSQL_INCLUDE_DIRS})
  if(NW_BUILD_SQLITE)
    target_link_libraries(nw_host_test_support PUBLIC nw_sqlite_engine)
  endif()
  target_compile_definitions(nw_host_test_support PUBLIC NW_ENABLE_SQLITE=$<BOOL:${NW_BUILD_SQLITE}> NW_ENABLE_POSTGRES=1 NW_HOST_TEST_HOOKS=1)

  function(nw_host_fixture name source)
    add_library(${name} SHARED "fixtures/${source}")
    target_link_libraries(${name} PRIVATE nw_host_test_support)
    set_target_properties(${name} PROPERTIES LIBRARY_OUTPUT_DIRECTORY "${NW_FIXTURE_OUTPUT}" RUNTIME_OUTPUT_DIRECTORY "${NW_FIXTURE_OUTPUT}")
    if(UNIX AND NOT APPLE)
      target_link_options(${name} PRIVATE "LINKER:--exclude-libs,ALL"
        "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/fixtures/exports.map")
      if(NOT NW_SANITIZERS)
        target_link_options(${name} PRIVATE "LINKER:--no-undefined")
      endif()
    endif()
  endfunction()
  nw_host_fixture(korda_postgres_fixture postgres.cpp)
  nw_fixture(korda_data_postgres postgres_component.cpp)
  add_executable(nw_postgres_fixture_tests postgres_fixture_tests.cpp)
  target_link_libraries(nw_postgres_fixture_tests PRIVATE nw_project_options korda_postgres_fixture)
  set_target_properties(nw_postgres_fixture_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
endif()
if(NOT (NW_BUILD_SQLITE AND NW_BUILD_POSTGRESQL))
  add_executable(nw_selected_abi database_abi.c)
  if(NW_BUILD_SQLITE)
    target_link_libraries(nw_selected_abi PRIVATE nw_project_options nw_database_sqlite)
  else()
    target_link_libraries(nw_selected_abi PRIVATE nw_project_options nw_database_postgresql)
  endif()
  target_compile_options(nw_selected_abi PRIVATE $<IF:$<C_COMPILER_ID:MSVC>,/UNDEBUG,-UNDEBUG>)
  add_test(NAME native.selected.abi COMMAND nw_selected_abi)
  return()
endif()

include(interop.cmake)

nw_native_test(contracts contracts_tests.cpp)
nw_native_test(query query_tests.cpp)
target_sources(nw_query_tests PRIVATE "${PROJECT_SOURCE_DIR}/native/cpp/src/query.cpp" "${PROJECT_SOURCE_DIR}/native/cpp/src/mutations.cpp")
nw_native_test(scalar scalar_tests.cpp)
target_sources(nw_scalar_tests PRIVATE "${PROJECT_SOURCE_DIR}/native/cpp/src/scalar.cpp")
nw_native_test(administration administration_tests.cpp)
target_sources(nw_administration_tests PRIVATE "${PROJECT_SOURCE_DIR}/native/cpp/src/administration.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/provider.cpp" "${PROJECT_SOURCE_DIR}/native/cpp/src/scalar.cpp")
target_compile_definitions(nw_administration_tests PRIVATE NW_ENABLE_SQLITE=1 NW_ENABLE_POSTGRES=1)
target_include_directories(nw_administration_tests SYSTEM PRIVATE ${PostgreSQL_INCLUDE_DIRS})
target_link_libraries(nw_administration_tests PRIVATE nw_sqlite_engine "${NW_LIBPQ_STATIC}"
  "${NW_PGCOMMON_SHARED_STATIC}" "${NW_PGPORT_SHARED_STATIC}" OpenSSL::SSL ZLIB::ZLIB ${CMAKE_DL_LIBS})
nw_native_test(provider provider_tests.cpp)
target_sources(nw_provider_tests PRIVATE "${PROJECT_SOURCE_DIR}/native/cpp/src/provider.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/scalar.cpp")
target_compile_definitions(nw_provider_tests PRIVATE NW_ENABLE_SQLITE=1 NW_ENABLE_POSTGRES=1)
target_include_directories(nw_provider_tests SYSTEM PRIVATE ${PostgreSQL_INCLUDE_DIRS})
target_link_libraries(nw_provider_tests PRIVATE nw_sqlite_engine "${NW_LIBPQ_STATIC}"
  "${NW_PGCOMMON_SHARED_STATIC}" "${NW_PGPORT_SHARED_STATIC}" OpenSSL::SSL ZLIB::ZLIB ${CMAKE_DL_LIBS})
nw_native_test(host_review host_review_tests.cpp)
target_sources(nw_host_review_tests PRIVATE "${PROJECT_SOURCE_DIR}/native/cpp/src/query.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/mutations.cpp" "${PROJECT_SOURCE_DIR}/native/cpp/src/scalar.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/provider.cpp" "${PROJECT_SOURCE_DIR}/native/cpp/src/host.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/administration.cpp")
target_compile_definitions(nw_host_review_tests PRIVATE NW_ENABLE_SQLITE=1 NW_ENABLE_POSTGRES=1 NW_HOST_TEST_HOOKS=1)
target_include_directories(nw_host_review_tests SYSTEM PRIVATE ${PostgreSQL_INCLUDE_DIRS})
target_link_libraries(nw_host_review_tests PRIVATE nw_sqlite_engine "${NW_LIBPQ_STATIC}"
  "${NW_PGCOMMON_SHARED_STATIC}" "${NW_PGPORT_SHARED_STATIC}" OpenSSL::SSL ZLIB::ZLIB ${CMAKE_DL_LIBS})

add_test(NAME native.contracts COMMAND nw_contracts_tests "${CMAKE_CURRENT_SOURCE_DIR}/golden/contracts")
add_test(NAME native.query COMMAND nw_query_tests "${PROJECT_SOURCE_DIR}")
add_test(NAME native.scalar COMMAND nw_scalar_tests)
add_test(NAME native.administration COMMAND nw_administration_tests)
add_test(NAME native.provider COMMAND nw_provider_tests)
add_test(NAME native.host_review COMMAND nw_host_review_tests)
set_tests_properties(native.contracts native.query native.scalar native.administration native.provider native.host_review
  PROPERTIES TIMEOUT 60 LABELS "native;contract")

nw_fixture(korda_query_binding_fixture query_binding.cpp)
target_sources(korda_query_binding_fixture PRIVATE "${PROJECT_SOURCE_DIR}/native/cpp/src/query.cpp"
  "${PROJECT_SOURCE_DIR}/native/cpp/src/mutations.cpp")
add_library(korda_verification_interop_c SHARED "${PROJECT_SOURCE_DIR}/src/Verification.Interop/layout.c")
target_link_libraries(korda_verification_interop_c PRIVATE nw_project_options)
set_target_properties(korda_verification_interop_c PROPERTIES
  C_VISIBILITY_PRESET default LIBRARY_OUTPUT_DIRECTORY "${NW_FIXTURE_OUTPUT}")

# Fault seams exist only in the retained B05/B06 maintainer bridges. The three
# product Host targets never receive NW_HOST_TEST_HOOKS.
nw_host_fixture(korda_transactions_fixture transactions.cpp)
nw_host_fixture(korda_streaming_fixture streaming.cpp)
foreach(component IN ITEMS transaction streaming)
  add_executable(nw_${component}_tests ${component}_tests.cpp)
  target_link_libraries(nw_${component}_tests PRIVATE nw_host_test_support)
  set_target_properties(nw_${component}_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
endforeach()
add_test(NAME native.transactions COMMAND nw_transaction_tests)
add_test(NAME native.streaming COMMAND nw_streaming_tests)
set_tests_properties(native.transactions native.streaming PROPERTIES TIMEOUT 120 LABELS "native;contract")
add_executable(nw_command_completion_tests command_completion_tests.cpp "${PROJECT_SOURCE_DIR}/native/cpp/src/host_abi.cpp")
target_link_libraries(nw_command_completion_tests PRIVATE nw_host_test_support)
set_target_properties(nw_command_completion_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
add_test(NAME native.command_completion COMMAND nw_command_completion_tests)
set_tests_properties(native.command_completion PROPERTIES TIMEOUT 120 LABELS "native;contract")
add_executable(nw_codec_roundtrip codec_roundtrip.cpp)
target_link_libraries(nw_codec_roundtrip PRIVATE nw_host_test_support)
set_target_properties(nw_codec_roundtrip PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
add_executable(nw_postgres_sync_tests postgres_sync_tests.cpp)
target_link_libraries(nw_postgres_sync_tests PRIVATE nw_host_test_support)
set_target_properties(nw_postgres_sync_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")

set(NW_COMPAT_PROFILES old new major layout semantics)
foreach(variant IN LISTS NW_COMPAT_PROFILES)
  list(FIND NW_COMPAT_PROFILES "${variant}" profile)
  nw_fixture(nw_compat_${variant} compatibility.cpp)
  target_compile_definitions(nw_compat_${variant} PRIVATE NW_COMPAT_PROFILE=${profile})
  if(variant STREQUAL "old" OR variant STREQUAL "new" OR variant STREQUAL "semantics")
    add_executable(nw_compatibility_${variant}_tests compatibility_tests.cpp)
    target_link_libraries(nw_compatibility_${variant}_tests PRIVATE nw_project_options nw_compat_${variant})
    set_target_properties(nw_compatibility_${variant}_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
    add_test(NAME native.compatibility.${variant} COMMAND nw_compatibility_${variant}_tests)
    set_tests_properties(native.compatibility.${variant} PROPERTIES TIMEOUT 10 LABELS "native;contract")
  endif()
endforeach()
set_tests_properties(native.compatibility.semantics PROPERTIES WILL_FAIL TRUE)

add_executable(nw_database_abi_c database_abi.c)
target_link_libraries(nw_database_abi_c PRIVATE korda_database_host)
foreach(target IN ITEMS nw_database_abi_c)
  target_link_libraries(${target} PRIVATE nw_project_options)
  set_target_properties(${target} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
  if(MSVC)
    target_compile_options(${target} PRIVATE /UNDEBUG)
  else()
    target_compile_options(${target} PRIVATE -UNDEBUG)
  endif()
endforeach()
add_test(NAME native.database.abi COMMAND nw_database_abi_c)
set_tests_properties(native.database.abi PROPERTIES TIMEOUT 10 LABELS "native;contract;abi")
nw_native_test(relations_abi relations_abi_tests.cpp)
target_link_libraries(nw_relations_abi_tests PRIVATE nw_database_sqlite nw_sqlite_engine)
add_test(NAME native.relations.abi COMMAND nw_relations_abi_tests)
set_tests_properties(native.relations.abi PROPERTIES TIMEOUT 60 LABELS "native;contract;abi")
# The ordinary Python interpreter cannot load an ASan-instrumented shared
# library without the sanitizer runtime. Instrumented C++ query/ABI tests remain
# enabled in that preset; this process-level status comparison is Release-only.
if(NOT NW_SANITIZERS)
  find_package(Python3 3.11 REQUIRED COMPONENTS Interpreter)
  add_test(NAME native.abi_status COMMAND "${Python3_EXECUTABLE}"
    "${PROJECT_SOURCE_DIR}/tests/Migration.Cpp/abi_probe.py"
    --native-library "$<TARGET_FILE:nw_database_sqlite>"
    --output "${NW_TEST_OUTPUT}/abi-status.json")
  set_tests_properties(native.abi_status PROPERTIES TIMEOUT 60 LABELS "native;contract;abi")
endif()

add_custom_target(nw_interop_fixtures DEPENDS nw_abi_conformance
  korda_interop_lifetime korda_interop_memory korda_errors_fixture
  korda_interop_callbacks korda_interop_async korda_interop_execution
  korda_verification_interop korda_verification_interop_c
  nw_async_downstream nw_execution_downstream nw_error_probe nw_error_abort_probe
  nw_abi_layout_c nw_abi_layout_cpp nw_abi_bootstrap_c)
foreach(variant IN LISTS NW_NEGOTIATION_VARIANTS)
  add_dependencies(nw_interop_fixtures nw_a02_${variant})
endforeach()

# Retain the bounded A20 C decoder/oracle fuzz profile, using the same pinned
# compiler and fixture targets as all other local and CI native verification.
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  add_executable(nw_interop_fuzz EXCLUDE_FROM_ALL "${PROJECT_SOURCE_DIR}/tests/Verification.Interop/fuzz/interop.c")
  add_executable(nw_interop_fuzz_negative EXCLUDE_FROM_ALL "${PROJECT_SOURCE_DIR}/tests/Verification.Interop/fuzz/interop.c")
  target_compile_definitions(nw_interop_fuzz_negative PRIVATE A20_ORACLE_NEGATIVE)
  add_executable(nw_interop_fatal EXCLUDE_FROM_ALL "${PROJECT_SOURCE_DIR}/tests/Verification.Interop/fuzz/fatal.c")
  foreach(target IN ITEMS nw_interop_fuzz nw_interop_fuzz_negative nw_interop_fatal)
    target_link_libraries(${target} PRIVATE nw_project_options korda_verification_interop)
    target_compile_options(${target} PRIVATE -g -O1 -fno-omit-frame-pointer -UNDEBUG)
    set_target_properties(${target} PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NW_TEST_OUTPUT}")
    if(target STREQUAL "nw_interop_fatal")
      target_compile_options(${target} PRIVATE -fsanitize=address,undefined)
      target_link_options(${target} PRIVATE -fsanitize=address,undefined)
    else()
      target_compile_options(${target} PRIVATE -fsanitize=fuzzer,address,undefined)
      target_link_options(${target} PRIVATE -fsanitize=fuzzer,address,undefined)
    endif()
  endforeach()
endif()
