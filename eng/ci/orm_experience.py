"""Exercise the order workflow outside the source tree with each standalone package."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SAMPLE = ROOT / "samples/api/orm-experience"
PROVIDERS = {"Korda.Database.Sqlite": "Sqlite", "Korda.Database.PostgreSql": "PostgreSql"}
REQUIRED = {
    "model-generates-reviewable-create-sql",
    "one-page-plus-one-detail-sql",
    "page-results-and-empty-association",
    "empty-batch-no-sql",
    "composite-cursor-ties-and-no-overfetch",
    "empty-final-page",
    "optional-customer-filter",
    "same-immutable-dto-for-fluent-and-parameterized-sql",
    "typed-grouped-aggregate-dto",
    "custom-id-decimal-json-and-nullable-mapping",
    "omitted-fields-versus-written-null",
    "stale-version-rejected",
    "insert-returning-and-confirmed-callback",
    "original-error-and-confirmed-rollback",
    "rollback-business-result",
    "precancel-does-not-invoke-callback",
    "destructive-migration-needs-explicit-selection",
    "explicit-destructive-script-is-reviewable",
    "existing-table-scaffold-written",
}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def exercise(work, selected, environment, evidence):
    """Publish/run, edit only managed DTO inputs, and prove the native image is reused."""
    evidence.mkdir(parents=True, exist_ok=True)
    for identity, provider in PROVIDERS.items():
        if identity not in selected:
            continue
        version = selected[identity]
        app = work / ("orders-" + provider.lower())
        app.mkdir()
        for path in SAMPLE.iterdir():
            if path.suffix in {".cs", ".csproj"}:
                shutil.copy2(path, app / path.name)
        project = app / "OrmExperience.csproj"
        if ET.parse(project).getroot().findall(".//ProjectReference"):
            raise ValueError("The business consumer must use PackageReference only")
        if ROOT == app or ROOT in app.parents:
            raise ValueError("The business consumer must be outside the source tree")
        rows = []
        commands = []
        receipt = evidence / (provider.lower() + ".json")
        generated = work / ("scaffold-" + provider.lower())
        baseline_hash = None
        models = app / "Models.cs"
        dto_before = sha(models)
        for revision in ("scaffold", "original", "edited"):
            edited = revision == "edited"
            if revision == "original":
                for name in ("CustomerCopy.cs", "OrderCopy.cs"):
                    scaffold = generated / name
                    if not scaffold.is_file():
                        raise ValueError("The bootstrap run did not scaffold " + name)
                    shutil.copy2(scaffold, app / name)
            if edited:
                # This is an actual application source edit. No package is rebuilt.
                old = "#if DTO_EDIT\n    , long Version\n#endif"
                source = models.read_text()
                if source.count(old) != 1:
                    raise ValueError("Expected exactly one DTO edit target")
                models.write_text(source.replace(old, "    , long Version"))
            output = app / ("out-" + revision)
            arguments = [
                "dotnet", "publish", str(project), "-c", "Release", "-r", "linux-x64",
                "-p:PublishAot=true", "-p:Provider=" + provider, "-p:KordaVersion=" + version,
                "-p:DtoEdit=" + str(edited).lower(), "-p:UseSharedCompilation=false",
                "-p:CompileScaffold=" + str(revision != "scaffold").lower(),
                "-p:IlcTreatWarningsAsErrors=true",
                "-m:1", "-nr:false", "-o", str(output),
            ]

            def run(label, command, extra=None):
                log = evidence / (provider.lower() + "-" + revision + "-" + label + ".log")
                print("RUN orders " + provider + " " + revision + " " + label, flush=True)
                started = time.monotonic()
                with log.open("w") as stream:
                    result = subprocess.run(
                        command, cwd=app, env=environment | (extra or {}),
                        stdout=stream, stderr=subprocess.STDOUT, timeout=900, check=False,
                    )
                commands.append({"revision": revision, "stage": label, "command": command,
                                 "exit_code": result.returncode, "seconds": round(time.monotonic() - started, 3),
                                 "log": log.name, "sha256": sha(log)})
                receipt.write_text(json.dumps({"package": identity, "version": version,
                                              "commands": commands, "rows": rows}, indent=2) + "\n")
                if result.returncode:
                    raise RuntimeError("Order workflow failed; inspect " + str(log))
                return log.read_text()

            run("publish", arguments)
            assets = json.loads((app / "obj/project.assets.json").read_text())
            actual = {key for key in assets["libraries"] if key.lower().startswith("korda.")}
            if actual != {identity + "/" + version}:
                raise ValueError("Unexpected Korda dependencies in the business consumer: " + str(actual))
            package = Path(environment["NUGET_PACKAGES"]) / identity.lower() / version.lower()
            archive = package / (identity.lower() + "." + version.lower() + ".nupkg")
            library = "libnw_database_" + ("postgresql" if provider == "PostgreSql" else "sqlite") + ".so"
            with zipfile.ZipFile(archive) as packed:
                native_hash = hashlib.sha256(packed.read("runtimes/linux-x64/native/" + library)).hexdigest()
            images = list(output.rglob(library))
            if not images or any(sha(path) != native_hash for path in images):
                raise ValueError("The published application replaced its packaged native provider")
            executable = output / "OrmExperience"
            if executable.read_bytes()[:4] != b"\x7fELF" or executable.with_suffix(".dll").exists():
                raise ValueError("Expected a standalone Native AOT ELF executable")
            text = run("execute", [str(executable)], {"DB_API_SCAFFOLD": str(generated)})
            results = [json.loads(line.removeprefix("DB_API_RESULT "))
                       for line in text.splitlines() if line.startswith("DB_API_RESULT ")]
            if len(results) != 1:
                raise ValueError("Expected one business result from the AOT process")
            result = results[0]
            required = REQUIRED | ({"scaffolded-entity-compiled-and-executed",
                                    "scaffolded-composite-key-compiled-and-executed"} if revision != "scaffold" else set())
            if (result["Provider"] != provider or result["Aot"] is not True
                    or result["Dto"] != ("edited" if edited else "original") or not required.issubset(result["Checks"])
                    or result["SqlExecutions"] != 2 or result["NativeSha256"] != native_hash):
                raise ValueError("Incomplete or inconsistent business receipt: " + json.dumps(result))
            if provider == "Sqlite" and result["ProtocolSubmissions"] != 0:
                raise ValueError("SQLite must not report libpq submissions")
            if provider == "PostgreSql" and result["ProtocolSubmissions"] < 2:
                raise ValueError("PostgreSQL did not record the actual protocol submissions")
            if baseline_hash is not None and baseline_hash != native_hash:
                raise ValueError("Changing a DTO changed the packaged native image")
            baseline_hash = native_hash
            rows.append(result | {"Phase": revision, "ExecutableSha256": sha(executable), "ModelsSha256": sha(models),
                                  "ScaffoldSha256": {name: sha(generated / name) for name in ("CustomerCopy.cs", "OrderCopy.cs")},
                                  "Sources": {path.name: sha(path) for path in sorted(app.glob("*.cs"))}, "ProjectReferences": 0})
        original, edited = rows[1:]
        if original["ModelsSha256"] == edited["ModelsSha256"] or original["ModelsSha256"] != dto_before:
            raise ValueError("The DTO source edit was not independently observed")
        if original["ExecutableSha256"] == edited["ExecutableSha256"]:
            raise ValueError("The edited DTO did not change the managed AOT executable")
        if original["Sources"].keys() != edited["Sources"].keys() or {
                name for name in original["Sources"] if original["Sources"][name] != edited["Sources"][name]
                } != {"Models.cs"} or original["ScaffoldSha256"] != edited["ScaffoldSha256"]:
            raise ValueError("The native reuse comparison must change only the DTO source")
        receipt.write_text(json.dumps({"task": "DB-API-01", "result": "passed", "package": identity,
                                      "version": version, "commands": commands, "rows": rows,
                                      "same_native_after_dto_edit": True}, indent=2) + "\n")
