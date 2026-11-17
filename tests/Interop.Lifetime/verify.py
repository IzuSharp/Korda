#!/usr/bin/env python3
"""Execute A03's real Linux x64 lifecycle, documentation and JIT/AOT consumers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if platform.system() != "Linux" or platform.machine() != "x86_64":
        parser.error("This execution profile is Linux x64 only.")
    output = args.output or ROOT / "artifacts/task03/A03" / time.strftime("run-%Y%m%dT%H%M%SZ", time.gmtime())
    output = output.resolve(); output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    env["LD_LIBRARY_PATH"] = str(ROOT / "target/release") + (":" + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    report = {"task":"A03", "platform":"linux-x64", "passed":False, "commands":[], "artifacts":{},
              "scope":"actual synchronous component; no assembled engine or Windows acceptance",
              "controlled_bounds":{"threads":3,"calls":2,"forced_phase_switches":8,"exhaustive":False}}
    def run(label, command):
        result = subprocess.run([str(c) for c in command],cwd=ROOT,env=env,capture_output=True,timeout=300)
        for suffix,data in [("stdout",result.stdout),("stderr",result.stderr)]: (output / (label + "." + suffix)).write_bytes(data)
        report["commands"].append({"label":label,"argv":list(map(str,command)),"exit":result.returncode,
                                  "stdout_sha256":hashlib.sha256(result.stdout).hexdigest(),"stderr_sha256":hashlib.sha256(result.stderr).hexdigest()})
        print(label, result.returncode, flush=True)
        if result.returncode: raise RuntimeError(result.stdout.decode(errors="replace")[-3000:] + result.stderr.decode(errors="replace")[-3000:])
        return result.stdout.decode(errors="replace")
    try:
        report["commit"] = run("commit",["git","rev-parse","HEAD"]).strip()
        report["dirty"] = bool(run("status",["git","status","--porcelain"]).strip())
        for label,cmd in [("dotnet",["dotnet","--version"]),("rustc",["rustc","-vV"]),("cargo",["cargo","--version"]),("clang",["clang","--version"])]: run(label,cmd)
        paths = run("source-files",["git","ls-files","--cached","--others","--exclude-standard","--",
                    "src/Interop.Lifetime","tests/Interop.Lifetime","native/interop/lifetime","samples/api/interop-lifetime",
                    "docs/guides/interop-lifetime.md","src/NativeWeave.Core","src/Interop.Abi","native/interop/abi","native/interop/errors",
                    "Directory.Build.props","global.json","rust-toolchain.toml","Cargo.toml","Cargo.lock"]).splitlines()
        report["source_sha256"] = {p:sha(ROOT/p) for p in sorted(set(paths)) if (ROOT/p).is_file()}
        run("native-format",["cargo","fmt","-p","nativeweave-interop-lifetime","--","--check"])
        run("native-tests",["cargo","test","--release","--locked","-p","nativeweave-interop-lifetime"])
        run("native-build",["cargo","build","--release","--locked","-p","nativeweave-interop-lifetime"])
        run("native-clippy",["cargo","clippy","--locked","-p","nativeweave-interop-lifetime","--all-targets","--","-D","warnings"])
        native = ROOT / "target/release/libnativeweave_interop_lifetime.so"
        report["artifacts"]["native"] = {"path":str(native),"sha256":sha(native)}
        common = ["-p:UseSharedCompilation=false","-nr:false","-m:1"]
        for label,project,assembly in [("tests","tests/Interop.Lifetime/Interop.Lifetime.Tests.csproj","NativeWeave.Interop.Lifetime.Tests"),
                                      ("sample","samples/api/interop-lifetime/Interop.Lifetime.Sample.csproj","NativeWeave.Interop.Lifetime.Sample")]:
            run(label+"-restore",["dotnet","restore",project,"--locked-mode",*common])
            run(label+"-build",["dotnet","build",project,"-c","Release","--no-restore",*common])
            dll=ROOT/Path(project).parent/"bin/Release/net11.0"/(assembly+".dll")
            report[label+"_jit"] = run(label+"-jit",["dotnet",dll]).strip()
            run(label+"-aot-restore",["dotnet","restore",project,"--locked-mode","-r","linux-x64","-p:PublishAot=true",*common])
            destination=output/(label+"-aot")
            run(label+"-aot-publish",["dotnet","publish",project,"-c","Release","-r","linux-x64","-p:PublishAot=true","-p:IlcTreatWarningsAsErrors=true","--no-restore","-o",destination,*common])
            executable=destination/assembly
            if (destination/(assembly+".dll")).exists(): raise RuntimeError("Native AOT executable required")
            report[label+"_aot"] = run(label+"-aot-run",[executable, *(["--require-aot"] if label=="tests" else [])]).strip()
            report["artifacts"][label+"_aot"]={"path":str(executable),"sha256":sha(executable)}
        project="tests/Interop.Lifetime/ApiCheck/ApiCheck.csproj"
        run("api-restore",["dotnet","restore",project,"--locked-mode",*common])
        run("api-build",["dotnet","build",project,"-c","Release","--no-restore",*common])
        sdk=Path(shutil.which("dotnet")).resolve().parent
        run("api-xml-reference",["dotnet",ROOT/"tests/Interop.Lifetime/ApiCheck/bin/Release/net11.0/NativeWeave.Interop.Lifetime.ApiCheck.dll",ROOT,sdk,ROOT/"src/NativeWeave.Core/bin/Release/net11.0/NativeWeave.Core.dll"])
        run("diff-check",["git","diff","--check"])
        for path,digest in report["source_sha256"].items():
            if sha(ROOT/path)!=digest: raise RuntimeError("Source changed during verification: "+path)
        report["source_unchanged"] = True; report["passed"] = True
    finally:
        (output/"result.json").write_text(json.dumps(report,indent=2)+"\n")
        print("Evidence:",output,flush=True)

if __name__ == "__main__": main()
