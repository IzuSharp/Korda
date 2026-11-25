#!/usr/bin/env python3
"""Publish an external Dapper.AOT consumer and preserve its complete restore graph."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]

def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--provider", choices=("sqlite","postgresql"), required=True)
    p.add_argument("--kind", choices=("probe","benchmark","query-phases"), default="probe")
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--manifest", type=Path, default=HERE.parent/"workload/queries-v3.json")
    a=p.parse_args(); app=a.output.resolve()
    manifest_path=a.manifest.resolve()
    if a.kind == "benchmark":
        validator_path=HERE.parent/"workload/check_dapper_contract.py"
        spec=importlib.util.spec_from_file_location("db_perf_contract",validator_path)
        validator=importlib.util.module_from_spec(spec); spec.loader.exec_module(validator)
        validator.validate(manifest_path,HERE/"Dapper/Contracts.generated.cs")
    if app.exists() or app == ROOT or ROOT in app.parents:
        raise ValueError("Use a fresh output directory outside the source tree")
    app.mkdir(parents=True)
    folder={"probe":"DapperProbe","benchmark":"Dapper","query-phases":"DapperQueryPhases"}[a.kind]
    for file in (HERE/folder).iterdir():
        if file.suffix in (".cs", ".csproj"): shutil.copy2(file, app/file.name)
    if a.kind == "benchmark":
        for file in (HERE/"Shared").glob("*.cs"): shutil.copy2(file,app/file.name)
    shutil.copy2(ROOT/"global.json", app/"global.json")
    (app/"NuGet.Config").write_text('<configuration><packageSources><clear/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    provider="Sqlite" if a.provider == "sqlite" else "PostgreSql"
    project=next(app.glob("*.csproj"))
    command=["dotnet","publish",str(project),"-c","Release","-r","linux-x64","-p:Provider="+provider,"-p:PublishAot=true","-p:IlcTreatWarningsAsErrors=true","-p:UseSharedCompilation=false","-m:1","-nr:false","-o",str(app/"out")]
    with (app/"publish.log").open("w") as log:
        result=subprocess.run(command,cwd=app,stdout=log,stderr=subprocess.STDOUT,check=False)
    receipt={"schemaVersion":1,"provider":a.provider,"kind":a.kind,"command":command,"exitCode":result.returncode,
        "sources":{file.name:sha(file) for file in sorted(app.glob("*.cs"))},"projectSha256":sha(project),"logSha256":sha(app/"publish.log")}
    if a.kind == "benchmark":
        shutil.copy2(manifest_path,app/"workload-manifest.json")
        receipt["workloadManifestSha256"]=sha(manifest_path)
        receipt["contractValidatorSha256"]=sha(validator_path)
    if result.returncode == 0:
        assets=app/"obj/project.assets.json"; shutil.copy2(assets,app/"project.assets.json")
        graph=json.loads(assets.read_text())
        receipt["assetsSha256"]=sha(assets)
        receipt["packages"]={}
        for identity, data in graph["libraries"].items():
            if data["type"] != "package": continue
            for folder in graph["packageFolders"]:
                package=Path(folder)/data["path"]/(identity.replace("/",".").lower()+".nupkg")
                if package.is_file():
                    receipt["packages"][identity]={"sha256":sha(package),"path":str(package)}; break
        binary=app/"out"/{"probe":"DbPerf.DapperProbe","benchmark":"DbPerf.Dapper","query-phases":"DbPerf.DapperQueryPhases"}[a.kind]
        if binary.read_bytes()[:4] != b"\x7fELF" or binary.with_suffix(".dll").exists():
            raise ValueError("Expected a standalone Native AOT ELF")
        receipt["executableSha256"]=sha(binary)
        generated=app/"obj/Release/net11.0/linux-x64/generated"
        if generated.is_dir(): shutil.copytree(generated,app/"generated")
        if a.kind == "query-phases":
            try:
                receipt["generatedSourceSha256"]={str(file.relative_to(app/"generated")):sha(file)
                    for file in sorted((app/"generated").rglob("*.cs"))}
                interceptors=list((app/"generated").rglob("*DapperQueryPhases.generated.cs"))
                if len(interceptors)!=1:
                    raise ValueError("Expected the actual Dapper.AOT generated phase consumer")
                text=interceptors[0].read_text(encoding="utf-8-sig")
                if not all(token in text for token in ("QueryUnbufferedAsync", "CommandFactory", "RowFactory", "Tokenize", "GetInt64", "GetString", "DbType.Int64")):
                    raise ValueError("The phase consumer lacks the expected generated typed parameter/DTO path")
            except Exception as error:
                receipt["postValidationError"]={"type":type(error).__name__,"message":str(error)}
                (app/"build-receipt.json").write_text(json.dumps(receipt,indent=2)+"\n")
                raise
    (app/"build-receipt.json").write_text(json.dumps(receipt,indent=2)+"\n")
    print(json.dumps(receipt,indent=2))
    raise SystemExit(result.returncode)

if __name__ == "__main__": main()
