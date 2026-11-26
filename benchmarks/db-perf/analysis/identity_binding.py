"""Bind actual archive bytes, public Git sources, packages and executed images."""

from __future__ import annotations

import hashlib
import io
from pathlib import PurePosixPath
import subprocess
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET
import zipfile

from archive_inputs import git_bytes, safe_name
from strict_io import field, natural, need, strict_json, valid_commit, valid_sha
from raw_metrics import number_equal


SCHEMA_COMMIT = "da153c22ef1093fccc3233ec76bfda96e6cc59df"
KNOWN_HARNESSES = {SCHEMA_COMMIT, "4ee348d8b9834a14327dcf68a105e34f52f81795"}
PACKAGE_COMMITS = {"before": "771bc77cbd68d3f932b99ed2497c0697bbba5f02", "after": "3d5ba3f9884308caa691718f73a74aa42be19d94"}
ROUTES = {"direct_cpp": "direct-native-cpp", "korda_before": "korda-public-fluent", "korda_after": "korda-public-fluent", "independent_csharp_orm": "independent-csharp-micro-orm-dapper-aot"}
PROVIDERS = ("postgresql", "sqlite")
CRITICAL_SOURCES = ["run.py", "managed/Korda/Measurement.cs", "managed/Dapper/Measurement.cs", "managed/Shared/CorrectnessOracle.cs",
                    "managed/Shared/OfferedLoad.cs", "managed/Shared/RecoveryMeasurement.cs", "managed/Shared/NativeAllocationProfile.cs",
                    "native/main.cpp", "native/allocation_profile.c", "native/allocation_profile.h"]


def sha(data):
    return hashlib.sha256(data).hexdigest()


class PublicSource:
    def __init__(self, repository, commit):
        self.repository, self.commit, self.cache = repository, valid_commit(commit), {}
        run = subprocess.run(["git", "ls-tree", "-r", "--name-only", self.commit], cwd=repository, capture_output=True, text=True)
        need(run.returncode == 0, "public_commit_missing", "Frozen public harness Git object is unavailable", unproven=True)
        self.paths = set(run.stdout.splitlines())

    def get(self, path):
        if path not in self.cache:
            self.cache[path] = git_bytes(self.repository, self.commit, path)
        return self.cache[path]

    def require(self, path, digest):
        need(sha(self.get(path)) == valid_sha(digest), "public_source_mismatch", "Copied build source differs from its public commit: " + path)
        return {"commit": self.commit, "path": path, "sha256": digest}


def package_binding(bundle, label, provider, public, package_repository=None):
    expected = bundle.json("request.json")[label]
    need(expected["sourceCommit"] == PACKAGE_COMMITS[label], "wrong_package_source", "Package source is not this campaign's fixed baseline/candidate")
    expected_provider = expected["providers"][provider]
    prefix = f"packages/{label}/{provider}/"
    release, artifact, workflow, commit = (bundle.json(prefix + name) for name in ("release.json", "artifact.json", "workflow-run.json", "source-commit.json"))
    need(release["source_commit"] == expected["sourceCommit"] and release["source_dirty"] is False and release["rid"] == "linux-x64",
         "release_source_identity", "Package receipt source/RID/cleanliness differs")
    need(commit["sha"] == release["source_commit"] and commit["tree"]["sha"] == release["source_tree"],
         "release_tree_identity", "Package Git identity differs from retained public commit metadata")
    run = subprocess.run(["git", "rev-parse", "--verify", release["source_commit"] + "^{tree}"], cwd=package_repository or public.repository, capture_output=True, text=True)
    need(run.returncode == 0 and run.stdout.strip() == release["source_tree"], "public_package_tree", "Package source tree does not match actual public Git object", unproven=True)
    need(artifact["id"] == expected_provider["artifactId"] and artifact["workflow_run"]["id"] == expected["runId"]
         and workflow["id"] == expected["runId"] and workflow["status"] == "completed" and workflow["conclusion"] == "success",
         "package_workflow_identity", "Candidate artifact/workflow identity differs")
    need(artifact["digest"] == "sha256:" + expected_provider["artifactZipSha256"] and bundle.files[prefix + "candidate.zip"]["sha256"] == expected_provider["artifactZipSha256"],
         "candidate_zip_identity", "Actual nested candidate ZIP differs from request/API metadata")
    need(artifact["size_in_bytes"] == bundle.files[prefix + "candidate.zip"]["bytes"], "candidate_zip_length", "Candidate API length differs from actual archive")
    package_id = "Korda.Database." + ("PostgreSql" if provider == "postgresql" else "Sqlite")
    packages = [item for item in release["packages"] if item["id"] == package_id]
    need(len(packages) == 1, "package_provider_identity", "Release does not uniquely identify the selected provider package")
    package = packages[0]
    version = expected_provider["version"]
    member = prefix + "feed/" + package_id + "." + version + ".nupkg"
    need(package["version"] == version and package["source_commit"] == expected["sourceCommit"] and package["upload_archive_sha256"] == expected_provider["nupkgSha256"],
         "package_identity", "Package ID/version/source/hash differs from frozen request")
    data = bundle.bytes(member)
    need(sha(data) == expected_provider["nupkgSha256"], "nupkg_actual_bytes", "Actual PackageReference input differs")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        need(len(names) == len(set(names)), "nupkg_duplicate_member", "Package ZIP repeats an entry")
        for name in names:
            safe_name(name)
        unsigned = {name: sha(archive.read(name)) for name in names if not name.endswith("/") and name != ".signature.p7s"}
        # The package's generated NuGet metadata path varies between archives;
        # release manifests deliberately omit only this generated .psmdcp entry.
        unsigned = {name: digest for name, digest in unsigned.items() if not name.startswith("package/services/metadata/core-properties/")}
        need(unsigned == package["unsigned_entries"], "nupkg_unsigned_payload", "Actual complete unsigned package payload differs from release")
        nuspec = ET.fromstring(archive.read(package_id + ".nuspec"))
        metadata = next(item for item in nuspec if item.tag.rsplit("}", 1)[-1] == "metadata")
        values = {item.tag.rsplit("}", 1)[-1]: item for item in metadata}
        need(values["id"].text == package_id and values["version"].text == version, "nuspec_identity", "Actual nuspec ID/version differs")
        need(values["repository"].attrib.get("commit") == expected["sourceCommit"], "nuspec_source", "Actual nuspec source commit differs")
        native = package["native"]
        native_data = archive.read(native["path"])
        need(sha(native_data) == native["sha256"] == expected_provider["nativeSha256"] and native["rid"] == "linux-x64",
             "nupkg_native_identity", "Actual provider native member differs")
        need(native_data[:6] == b"\x7fELF\x02\x01" and int.from_bytes(native_data[18:20], "little") == 62,
             "nupkg_native_architecture", "Provider native payload is not ELF x64")
    return {"source_commit": expected["sourceCommit"], "source_tree": release["source_tree"], "id": package_id, "version": version,
        "artifact_id": artifact["id"], "workflow_run_id": workflow["id"], "zip_sha256": expected_provider["artifactZipSha256"],
        "nupkg_sha256": expected_provider["nupkgSha256"], "native_sha256": native["sha256"], "unsigned_members_verified": len(unsigned),
        "scope": "actual runtime-archived candidate ZIP/nupkg/member bytes and public source tree; publication to NuGet is a separate delivery gate"}


def bind_runtime(bundle, repository, package_repository=None):
    manifest, preparation = bundle.manifest, bundle.preparation
    need(manifest.get("task") == "DB-Perf-01" and manifest.get("schemaVersion") == 1, "runtime_schema", "Unknown runtime manifest schema")
    need(manifest.get("harnessCommit") in KNOWN_HARNESSES, "unreviewed_controller_version", "Controller/measurement source profile requires an explicit reviewed adapter", unproven=True)
    public = PublicSource(repository, manifest["harnessCommit"])
    schema = PublicSource(repository, SCHEMA_COMMIT)
    producer_sources = []
    for name in CRITICAL_SOURCES:
        path = "benchmarks/db-perf/" + name
        need(public.get(path) == schema.get(path), "unrecognized_measurement_producer", "Measurement producer changed; this arithmetic adapter needs explicit source review: " + path, unproven=True)
        producer_sources.append(public.require(path, sha(public.get(path))))
    request = bundle.json("request.json")
    request_hash = sha(bundle.bytes("request.json"))
    need(request_hash == manifest["requestSha256"] == preparation["requestSha256"] and request["harnessCommit"] == manifest["harnessCommit"] == preparation["harnessCommit"],
         "runtime_request_identity", "Actual request/harness differs from runtime build identity")
    need(bundle.tar_gzip_sha256 == preparation["sha256"] and bundle.manifest_sha256 == preparation["bundleManifestSha256"],
         "runtime_preparation_identity", "Actual runtime archive differs from preparation receipt")
    controllers = {}
    for name in ("run.py", "campaign.py", "campaign_build.py", "plan_diagnostics.py"):
        digest = sha(bundle.bytes("harness/" + name))
        public.require("benchmarks/db-perf/" + name, digest)
        controllers[name] = digest
    workloads = {}
    for key, destination in (("workload", manifest["workload"]), ("heavyWorkload", manifest["heavyWorkload"])):
        specification = request[key]
        need(bundle.files[destination]["sha256"] == specification["sha256"], "runtime_workload_identity", "Actual workload differs from request")
        public.require(specification["path"], specification["sha256"])
        workloads["primary" if key == "workload" else specification["id"]] = {"sha256": specification["sha256"], "cases": specification["cases"], "manifest": bundle.json(destination),
            "warmup_operations": specification.get("warmupOperations", request["measurement"]["warmupOperations"]), "runtime_path": destination}
    packages = {label: {provider: package_binding(bundle, label, provider, public, package_repository) for provider in PROVIDERS} for label in PACKAGE_COMMITS}
    applications = {}
    for provider in PROVIDERS:
        for route in ROUTES:
            key = provider + "/" + route
            executable = manifest["applications"][key]
            if route == "direct_cpp":
                receipt = bundle.json("native/build-receipt.json")
                need(receipt["language"] == "C++20" and receipt["configuration"] == "Release -O3 -DNDEBUG" and all(code == 0 for code in receipt["exitCodes"]),
                     "native_build_configuration", "Direct C++ was not a successful Release build")
                for name, digest in receipt["sources"].items():
                    public.require("benchmarks/db-perf/native/" + name, digest)
                for name, digest in receipt["outputs"].items():
                    bundle.require_elf("native/" + name, digest)
                for name, digest in receipt["logs"].items():
                    need(bundle.files["native/" + PurePosixPath(name).name]["sha256"] == digest, "native_build_log_identity", "Native build log changed")
                applications[key] = {"executable": executable, "sha256": bundle.require_elf(executable, receipt["outputs"]["db-perf-native"]),
                    "build_receipt_sha256": bundle.files["native/build-receipt.json"]["sha256"], "native_sqlite_sha256": receipt["outputs"]["libsqlite3.so"],
                    "source_commit": public.commit, "compiler_sha256_as_recorded": receipt["compilerSha256"], "native_input_hashes_as_recorded": receipt["inputs"],
                    "native_build_input_scope": "receipt hashes retained; actual compiler/static archive byte closure requires the separate input archive review"}
                continue
            prefix = f"apps/{provider}/{route}/"
            receipt = bundle.json(prefix + "build-receipt.json")
            need(field(receipt, "exitCode") == 0 and receipt["provider"] == provider, "managed_build_failed", "Managed build receipt failed/provider differs")
            argv = receipt["command"]
            need("publish" in argv and "-p:PublishAot=true" in argv and "linux-x64" in argv and "Release" in argv, "managed_not_aot_release", "Actual build command is not Linux x64 Native AOT Release")
            family = "Dapper" if route == "independent_csharp_orm" else "Korda"
            source_records = []
            for name, digest in receipt["sources"].items():
                need(bundle.files[prefix + name]["sha256"] == digest, "consumer_source_identity", "Copied external consumer source differs from build receipt")
                possibilities = [f"benchmarks/db-perf/managed/{part}/{name}" for part in (family, "Shared")]
                matching = [path for path in possibilities if path in public.paths and sha(public.get(path)) == digest]
                need(bool(matching), "consumer_public_source_identity", "Copied external consumer source has no matching public source at the bound harness commit: " + name)
                source_records.append(public.require(matching[0], digest))
            project_name = "DbPerf." + family + ".csproj"
            project = bundle.bytes(prefix + project_name)
            need(sha(project) == field(receipt, "projectSha256"), "consumer_project_identity", "External project differs from build receipt")
            public.require(f"benchmarks/db-perf/managed/{family}/" + project_name, sha(project))
            project_xml = ET.fromstring(project)
            need(not any(node.tag.rsplit("}", 1)[-1] == "ProjectReference" for node in project_xml.iter()), "consumer_project_reference", "Consumer depends directly on repository projects")
            assets = bundle.json(prefix + "project.assets.json")
            need(bundle.files[prefix + "project.assets.json"]["sha256"] == field(receipt, "assetsSha256"), "consumer_assets_identity", "Restored dependency graph differs from build receipt")
            log_sha = field(receipt, "logSha256", "publishLogSha256")
            need(bundle.files[prefix + "publish.log"]["sha256"] == log_sha, "managed_publish_log_identity", "Actual AOT publish log differs")
            item = {"executable": executable, "sha256": bundle.require_elf(executable, field(receipt, "executableSha256")),
                "build_receipt_sha256": bundle.files[prefix + "build-receipt.json"]["sha256"], "consumer_source_commit": public.commit,
                "consumer_sources": source_records, "assets_sha256": field(receipt, "assetsSha256")}
            if family == "Korda":
                package = packages[route.removeprefix("korda_")][provider]
                need(field(receipt, "packageSha256") == package["nupkg_sha256"] and receipt["package"] == package["id"] and receipt["version"] == package["version"],
                     "consumer_package_identity", "External AOT build used another candidate package")
                kordas = [name for name in assets["libraries"] if name.startswith("Korda.")]
                need(kordas == [package["id"] + "/" + package["version"]], "consumer_package_graph", "External consumer restored another or multiple top-level Korda packages")
                native_name = "libnw_database_" + ("postgresql" if provider == "postgresql" else "sqlite") + ".so"
                bundle.require_elf(prefix + "out/" + native_name, package["native_sha256"])
                need(receipt["native_images"].get(native_name) == package["native_sha256"], "consumer_native_image", "External build native provider differs")
                item.update(package=package, native_relative_path=prefix + "out/" + native_name)
            else:
                expected_orm = {"Dapper/2.1.100", "Dapper.AOT/1.1.0", "Npgsql/11.0.0" if provider == "postgresql" else "Microsoft.Data.Sqlite.Core/11.0.0"}
                need(expected_orm <= set(assets["libraries"]) and expected_orm <= set(receipt["packages"]), "independent_orm_version", "Actual independent ORM/provider package versions differ")
                item.update(orm_packages={key: receipt["packages"][key] for key in sorted(expected_orm)},
                    orm_package_scope="actual restored assets + build-reported NuGet package hashes; third-party nupkg bytes are not in this runtime archive")
            applications[key] = item
    for provider in PROVIDERS:
        before, after = (applications[provider + "/korda_" + label] for label in ("before", "after"))
        need(before["consumer_sources"] == after["consumer_sources"], "before_after_consumer_changed", "Before/after did not use identical external consumer source")
    postgres_binary = manifest["postgresBin"] + "/postgres"
    return {"request": request, "request_sha256": request_hash, "harness_commit": public.commit, "schema_commit": SCHEMA_COMMIT,
        "postgres_binary": {"path": postgres_binary, "sha256": bundle.require_elf(postgres_binary, bundle.files[postgres_binary]["sha256"])},
        "allocator_profiler": {"path": "native/libdb-perf-alloc.so", "sha256": bundle.require_elf("native/libdb-perf-alloc.so", bundle.files["native/libdb-perf-alloc.so"]["sha256"])},
        "query_plan_library": {"path": "auto-explain/auto_explain.so", "sha256": bundle.require_elf("auto-explain/auto_explain.so", bundle.files["auto-explain/auto_explain.so"]["sha256"])},
        "producer_sources": producer_sources, "controller_sources": controllers, "packages": packages, "applications": applications, "workloads": workloads}


def argv_options(argv):
    need(isinstance(argv, list) and argv and all(isinstance(part, str) for part in argv), "command_argv_shape", "Executed argv is absent")
    result, index = {}, 1
    while index < len(argv):
        key = argv[index]
        need(key.startswith("--"), "command_option_shape", "Malformed command option")
        if key in ("--explain", "--initialize", "--verify", "--lifecycle-checks", "--recovery-checks", "--empty-driver", "--allocation-profile"):
            result[key], index = True, index + 1
        else:
            need(index + 1 < len(argv) and not argv[index + 1].startswith("--"), "command_option_shape", "Command option has no value")
            # Both public parsers apply options in order. Diagnostic commands
            # deliberately append warmup/idle overrides; preserve that meaning.
            result[key], index = argv[index + 1], index + 2
    return result


def bind_cell(bundle_binding, normalized, raw, command, plan, runner, campaign, owner=None):
    provider, route = normalized["provider"], normalized["route"]
    need(provider in PROVIDERS and route in ROUTES, "unknown_cell_identity", "Unknown route/provider cell")
    raw_provider = provider if route == "direct_cpp" else (("Npgsql" if provider == "postgresql" else "Microsoft.Data.Sqlite.Core") if route == "independent_csharp_orm" else ("PostgreSql" if provider == "postgresql" else "Sqlite"))
    expected_route = ROUTES[route]
    if normalized.get("diagnostic") == "same-process-recovery" and route != "direct_cpp":
        expected_route = "dapper-aot-recovery" if route == "independent_csharp_orm" else "korda-public-fluent-recovery"
    need(field(raw, "provider") == raw_provider and field(raw, "route") == expected_route, "raw_cell_identity", "Wrapper route/provider differs from actual raw producer")
    application = bundle_binding["applications"][provider + "/" + route]
    need(field(raw, "executableSha256") == application["sha256"], "raw_executable_identity", "Raw executed image differs from this route/provider's actual runtime ELF")
    need(runner["requestSha256"] == campaign["requestSha256"] == bundle_binding["request_sha256"]
         and runner["bundleManifestSha256"] == campaign["bundleManifestSha256"]
         and runner["controllerSources"] == bundle_binding["controller_sources"] and runner["environmentIdentity"] == campaign["environmentIdentity"],
         "runner_binding_mismatch", "Runner request/bundle/controller/VM binding differs")
    need(plan["provider"] == provider and len(plan["commands"]) == 1, "plan_cell_identity", "Execution plan is not one matching provider workload")
    item = plan["commands"][0]
    affinity = ",".join(str(cpu) for cpu in bundle_binding["request"]["admission"]["clientCpus"])
    need(item["cpus"] == affinity and command["argv"][:3] == ["taskset", "-c", affinity], "client_affinity_binding", "Actual taskset client affinity differs from frozen request")
    argv = command["argv"][3:]
    planned_argv = item["argv"]
    if normalized.get("diagnostic") == "native-allocation":
        need(argv[0] == planned_argv[0] == "env" and argv[:4] == planned_argv[:4], "native_profile_launcher", "Allocator diagnostic did not use its recorded preload launcher")
        assignments = dict(part.split("=", 1) for part in argv[1:4])
        need(set(assignments) == {"LD_PRELOAD", "DB_PERF_ALLOC_PROFILE_LIBRARY", "DB_PERF_ALLOC_CAPACITY"}, "native_profile_environment", "Allocator diagnostic has missing/foreign preload settings")
        need(assignments["LD_PRELOAD"] == assignments["DB_PERF_ALLOC_PROFILE_LIBRARY"] and assignments["LD_PRELOAD"].endswith("/bundle/native/libdb-perf-alloc.so")
             and assignments["DB_PERF_ALLOC_CAPACITY"] == str(bundle_binding["request"]["diagnostics"]["nativeAllocation"]["tableCapacity"]),
             "native_profile_preload_binding", "Allocator preload path/capacity differs from frozen input")
        argv, planned_argv = argv[4:], planned_argv[4:]
    options, planned_options = argv_options(argv), argv_options(planned_argv)
    need(argv[0].endswith("/bundle/" + application["executable"]) and argv[0] == planned_argv[0], "command_executable_binding", "Command launched another route/provider image")
    runtime_root = argv[0][:-len(application["executable"])]
    if normalized.get("diagnostic") == "native-allocation":
        need(assignments["LD_PRELOAD"] == runtime_root + "native/libdb-perf-alloc.so" and options.get("--allocation-profile") is True,
             "native_profile_runtime_root", "Profiler was not loaded from the same frozen runtime as its consumer")
    need({key: value for key, value in options.items() if key != "--connection"} == {key: value for key, value in planned_options.items() if key != "--connection"},
         "command_plan_mismatch", "Actual command options differ from immutable execution plan")
    recovery_native = normalized.get("diagnostic") == "same-process-recovery" and route == "direct_cpp"
    native_phase_values = [field(item, "measurement") for item in field(raw, "phases")] if recovery_native else [raw]
    session_payload = native_phase_values[0] if recovery_native else raw
    need(options["--mode"] == normalized["constructionMode"] == field(raw, "mode") and options["--session"] == field(session_payload, "sessionMode") == "persistent",
         "query_contract_binding", "Query construction/session contract differs")
    need(options["--cases"].split(",") == normalized["workloadCaseIds"] and int(options["--concurrency"]) == normalized["concurrency"] == field(raw, "concurrency"),
         "workload_command_binding", "Actual selected cases/concurrency differ from wrapper/raw")
    number_equal(float(options.get("--seconds", "0")), normalized["seconds"], "command seconds")
    recorded_rate = options.get("--arrival-rate", options.get("--rate"))
    need((float(recorded_rate) if recorded_rate is not None else None) == normalized.get("rate"), "rate_command_binding", "Actual offered rate differs from wrapper")
    workload = bundle_binding["workloads"][normalized["workloadId"]]
    need(normalized["workloadSha256"] == workload["sha256"] and set(normalized["workloadCaseIds"]) <= set(workload["cases"]),
         "cell_workload_binding", "Cell workload differs from actual runtime contract")
    expected_warmup = (bundle_binding["request"]["diagnostics"]["queryPlans"]["warmupOperations"]
                       if normalized.get("diagnostic") == "query-plan" else workload["warmup_operations"])
    need(int(options["--warmup"]) == expected_warmup == natural(field(session_payload, "warmupPerWorker"), "raw warmup operations"),
         "warmup_cost_contract", "Actual warmup differs from the matching primary/heavy/diagnostic contract")
    expected_idle = 0 if normalized.get("diagnostic") == "query-plan" else bundle_binding["request"]["measurement"]["idleSeconds"]
    number_equal(float(options["--idle-seconds"]), expected_idle, "bound idle seconds")
    if normalized.get("rate") is not None or normalized.get("diagnostic") == "same-process-recovery":
        expected_queue = bundle_binding["request"]["measurement"]["queuePerWorker"] * normalized["concurrency"]
        need(int(options["--queue-capacity"]) == expected_queue, "queue_cost_contract", "Actual queue capacity differs from frozen worker resource budget")
        if normalized.get("diagnostic") != "same-process-recovery":
            need(field(field(raw, "offeredLoad"), "queueCapacity") == expected_queue, "raw_queue_cost_contract", "Actual raw queue capacity differs from command/request")
        else:
            settings = bundle_binding["request"]["diagnostics"]["sameProcessRecovery"]
            need(options.get("--recovery-checks") is True, "recovery_command_flag", "Recovery diagnostic was not executed by its actual CLI")
            for option, expected in (("--recovery-rate", campaign["fixedRates"][provider]), ("--recovery-seconds", settings["seconds"]),
                                     ("--overload-rate", settings["overloadRate"]), ("--overload-seconds", settings["overloadSeconds"])):
                number_equal(float(options[option]), expected, "bound recovery " + option)
            number_equal(normalized["seconds"], settings["seconds"] * 2 + settings["overloadSeconds"], "bound recovery total seconds")
            phases = native_phase_values if recovery_native else field(raw, "phases")
            need(all(field(field(value, "offeredLoad"), "queueCapacity") == expected_queue for value in phases),
                 "recovery_raw_queue_cost_contract", "A recovery phase changed the frozen queue resource bound")
            if not recovery_native:
                need(field(raw, "queueCapacity") == expected_queue, "recovery_queue_envelope", "Recovery envelope changed queue capacity")
                for name, expected in (("fixedRequestsPerSecond", campaign["fixedRates"][provider]), ("fixedSeconds", settings["seconds"]),
                                       ("overloadRequestsPerSecond", settings["overloadRate"]), ("overloadSeconds", settings["overloadSeconds"])):
                    number_equal(field(raw, name), expected, "recovery envelope " + name)
    if route != "direct_cpp":
        dataset = workload["manifest"]["dataset"]
        for raw_name, option, contract_name, default in (("requestedRows", "--rows", "requestedRows", 32), ("batchRows", "--batch-rows", "batchRows", 32),
                                                       ("tenants", "--tenants", "tenants", None), ("ordersPerTenant", "--orders-per-tenant", "ordersPerTenant", None)):
            expected = natural(dataset.get(contract_name, default), "dataset " + contract_name, 1)
            need(int(options[option]) == natural(field(raw, raw_name), "raw " + raw_name, 1) == expected,
                 "dataset_or_batch_cost_contract", "Actual raw/CLI dataset or batch size differs from the frozen workload: " + raw_name)
    need(field(raw, "emptyDriver", optional=True, default=False) is False, "empty_driver_performance", "An empty driver cannot claim real business performance")
    if route == "direct_cpp":
        need(field(raw, "buildReceiptSha256") == application["build_receipt_sha256"] and field(raw, "manifestSha256") == field(raw, "manifestSha256After") == workload["sha256"]
             and field(raw, "inputImmutable") is True and options["--manifest"] == runtime_root + workload["runtime_path"],
             "native_raw_build_binding", "Native raw input/build hashes differ")
        need(field(raw, "loadedSqliteSha256") == application["native_sqlite_sha256"], "native_loaded_sqlite", "Actually loaded SQLite bytes differ")
        native_contract = {"physicalPrepare": field(native_phase_values[0], "physicalPreparation"), "protocolConfirmation": field(native_phase_values[0], "protocolConfirmation")}
        need(all(field(value, "physicalPreparation") == native_contract["physicalPrepare"] and field(value, "protocolConfirmation") == native_contract["protocolConfirmation"] for value in native_phase_values),
             "native_phase_contract_changed", "Retained recovery phases changed preparation/protocol policy")
        need(native_contract == normalized["nativeContract"] and options["--physical-prepare"] == native_contract["physicalPrepare"]
             and options["--protocol-confirmation"] == native_contract["protocolConfirmation"],
             "native_prepare_protocol_binding", "Raw physical preparation/protocol policy differs from command/wrapper")
        name = normalized["name"]
        expected_contract = (bundle_binding["request"]["nativeLayers"][int(name.removeprefix("native-layer-"))]
                             if name.startswith("native-layer-") else bundle_binding["request"]["nativeBaseline"])
        if normalized["constructionMode"] == "dynamic":
            expected_contract = dict(expected_contract, physicalPrepare="per-operation")
        need(native_contract == expected_contract, "native_baseline_layer_binding", "Attribution layer was relabeled as the primary direct-native column")
    elif route.startswith("korda_"):
        need(field(raw, "nativeAot") is True and field(raw, "packageVersion") == application["package"]["version"] and field(raw, "loadedNativeSha256") == application["package"]["native_sha256"],
             "korda_loaded_package", "Raw AOT/package/native image differs from verified actual input")
        need(field(raw, "loadedNativePath").endswith("/bundle/" + application["native_relative_path"]), "korda_loaded_path", "Loaded native provider path belongs to another input")
    else:
        need(field(raw, "nativeAot") is True, "orm_not_aot", "Independent ORM consumer is not Native AOT")
        if provider == "sqlite":
            expected = bundle_binding["applications"]["sqlite/direct_cpp"]["native_sqlite_sha256"]
            need(field(raw, "loadedNativeSha256") == expected, "orm_sqlite_library", "Independent ORM loaded another SQLite engine")
    if provider == "postgresql":
        cpus = ",".join(str(cpu) for cpu in bundle_binding["request"]["admission"]["serverCpus"])
        need(plan["postgres"]["cpus"] == cpus and plan["postgres"]["cpuAccounting"] == "task-owned-cgroup-v2", "server_affinity_binding", "PostgreSQL CPU partition/accounting differs")
        need(isinstance(owner, dict) and owner.get("argv", [])[:3] == ["taskset", "-c", cpus],
             "actual_server_affinity_binding", "Actual PostgreSQL owner launch affinity differs from frozen server CPUs")
        settings = campaign["postgresCpuAccountingSettings"]
        need(owner["serverCpuGroup"]["descriptor"]["controls"] == settings["groupControls"]
             and owner["serverCpuGroup"]["parentControlsBefore"] == settings["parentControls"],
             "server_group_settings_changed", "Actual owned server CPU/memory controls differ between campaign cells")
        actual = owner["argv"]
        need(len(actual) > 4 and actual[3] == plan["postgres"]["bin"] + "/postgres"
             and actual[3] == runtime_root + bundle_binding["postgres_binary"]["path"],
             "actual_server_binary_binding", "Actual PostgreSQL owner command launches another binary installation")
        script = 'set -eu\nprintf "%s\\n" "$$" | sudo -n tee -- "$1/cgroup.procs" >/dev/null\nshift\nexec "$@"'
        expected_launch = ["/bin/sh", "-c", script, "db-perf-postgres-cgroup", owner["serverCpuGroup"]["descriptor"]["path"], *actual]
        need(owner.get("launcherArgv") == expected_launch, "actual_server_launcher_binding", "Actual group-entry launcher does not execute exactly the recorded owned PostgreSQL command")
        server_options, server_settings, index = {}, {}, 4
        while index < len(actual):
            need(index + 1 < len(actual), "server_argument_missing", "PostgreSQL option lacks a value")
            key, value = actual[index:index + 2]
            if key == "-c":
                setting, separator, selected_value = value.partition("=")
                need(bool(separator), "server_setting_shape", "PostgreSQL setting is not key=value")
                need(setting not in server_settings, "server_duplicate_setting", "Actual PostgreSQL command repeats a source-defined setting")
                server_settings[setting] = selected_value
            else:
                need(key not in server_options, "server_duplicate_option", "PostgreSQL command repeats a structural option")
                server_options[key] = value
            index += 2
        need(set(server_options) == {"-D", "-h", "-p"} and server_options.get("-D") == plan["postgres"]["data"] and server_options.get("-h") == "127.0.0.1", "server_data_endpoint_binding", "Actual PostgreSQL structural options/data directory/listen endpoint differ")
        connection = urlsplit(options["--connection"])
        need(connection.scheme == "postgresql" and connection.hostname == "127.0.0.1" and connection.path == "/db_perf_01" and connection.username == "korda"
             and str(connection.port) == server_options.get("-p"), "client_server_endpoint_binding", "Actual client connection targets another database/server port")
        required_settings = {"unix_socket_directories": "", "max_connections": str(plan["postgres"]["maxConnections"]),
            "fsync": "on", "synchronous_commit": "on", "full_page_writes": "on", "timezone": "UTC", "shared_buffers": "128MB", "default_transaction_isolation": "read committed"}
        if normalized.get("diagnostic") == "query-plan":
            library = bundle_binding["query_plan_library"]
            expected_library = runtime_root + library["path"]
            need(plan["postgres"].get("queryPlans") == {"library": expected_library, "sha256": library["sha256"]},
                 "query_plan_library_binding", "PostgreSQL executor plan logging uses another actual extension library")
            required_settings.update({"session_preload_libraries": expected_library, "logging_collector": "on", "log_destination": "jsonlog",
                "log_directory": PurePosixPath(plan["postgres"]["data"]).parent.as_posix(), "log_filename": "postgres-query-plans.log",
                "log_rotation_age": "0", "log_rotation_size": "0", "log_statement": "all", "log_parameter_max_length": "-1",
                "auto_explain.log_min_duration": "0", "auto_explain.log_parameter_max_length": "-1", "auto_explain.log_format": "json",
                "auto_explain.log_analyze": "off", "auto_explain.log_timing": "off", "auto_explain.log_settings": "on",
                "auto_explain.log_nested_statements": "on", "auto_explain.sample_rate": "1"})
        else:
            need(plan["postgres"].get("queryPlans") is None, "timing_plan_instrumentation", "Ordinary timing command has uncontracted plan instrumentation")
        need(server_settings == required_settings, "server_durability_contract", "Actual PostgreSQL startup settings differ or include uncontracted tuning/instrumentation")
        need(plan["postgres"]["maxConnections"] == max(32, max(bundle_binding["request"]["measurement"]["capacityConcurrency"]) + 8),
             "server_connection_capacity", "Actual PostgreSQL connection capacity differs from frozen campaign bounds")
    else:
        need(options["--connection"] == plan["connection"], "sqlite_database_binding", "Actual SQLite connection differs from its private planned database")
    return {"provider": provider, "route": route, "executable_sha256": application["sha256"], "harness_commit": bundle_binding["harness_commit"],
        "package_source_commit": application.get("package", {}).get("source_commit"), "native_sha256": application.get("package", {}).get("native_sha256"),
        "runtime_root_as_executed": runtime_root, "native_contract": normalized["nativeContract"], "request_sha256": bundle_binding["request_sha256"], "environment_identity": runner["environmentIdentity"],
        "affinity_scope": "actual taskset argv and source-bound runner; per-thread /proc Cpus_allowed_list was not serialized"}
