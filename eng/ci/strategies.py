"""Module strategies. Commands run on the current runner; resources describe what YAML must prepare."""
from dataclasses import dataclass
from pathlib import Path
import json
import os
import shlex
import subprocess
import sys
import time

from language import purpose

PROVIDERS = ("postgresql", "sqlite")
DESIGN = "artifacts/frozen-design"
FULL_NATIVE = "artifacts/cmake/linux-x64-release"

@dataclass(frozen=True)
class Action:
    name: str
    resources: tuple = ()
    needs: tuple = ()

    def run(self, ctx):
        raise NotImplementedError(self.name)

@dataclass(frozen=True)
class Command(Action):
    commands: tuple = ()

    def run(self, ctx):
        for command in self.commands:
            ctx.call([str(word).format_map(ctx.variables) for word in command])

@dataclass(frozen=True)
class Fixture(Action):
    def run(self, ctx):
        target = self.name.split(".")[-1]
        build = Path("artifacts/fixture-tests") / target
        corpus = target == "interop-corpus"
        ctx.call(["cmake", "-S", "eng/ci/fixtures", "-B", build, "-DCMAKE_BUILD_TYPE=Release",
                  "-DCMAKE_C_COMPILER=clang", "-DCMAKE_CXX_COMPILER=clang++",
                  "-DCHECK_INTEROP_CORPUS=" + ("ON" if corpus else "OFF")])
        ctx.call(["cmake", "--build", build, "--target", target])
        inputs = []
        if corpus:
            cases = json.loads(Path("tests/Verification.Interop/corpus/seeds.json").read_text())["cases"]
            if not cases:
                raise ValueError("Empty interop fixture corpus")
            for index, case in enumerate(cases):
                path = build / f"seed-{index}.bin"
                path.write_bytes(bytes.fromhex(case["hex"]))
                inputs.append(path)
        ctx.call([build / target, *inputs])

@dataclass(frozen=True)
class Interop(Action):
    component: str = ""

    def run(self, ctx):
        args = ["--design-root", DESIGN, "--plan-root", "artifacts/frozen-plan",
                "--output", "artifacts/interop-negotiation-tests"] if self.component == "Negotiation" else []
        ctx.call([sys.executable, f"tests/Interop.{self.component}/verify.py", *args],
                 env={"NW_CMAKE_PRESET": "interop"})

def py(*args):
    return ("python3", *args)

def recipes():
    steps = {}
    def add(name, *commands, needs=(), resources=()):
        steps[name] = Command(name, tuple(resources), tuple(needs), commands)
    native = ("native", "pg-tools")
    add("database.native", py("eng/ci/native_graph.py", "query", FULL_NATIVE),
        py("eng/build.py", "native"), resources=native)
    add("database.dependencies", py("eng/ci/native_graph.py", "verify", FULL_NATIVE,
        "nw_database_sqlite", "nw_database_postgresql"), needs=("database.native",))
    add("database.build", py("eng/build.py", "workspace"), resources=("dotnet",), needs=("database.native",))
    add("database.api", py("eng/build.py", "api"), needs=("database.build",), resources=("design", "scope-design"))
    add("database.regressions", py("eng/build.py", "regressions"), needs=("database.native",),
        resources=("dotnet", "design", "plan", "pg-server"))
    add("database.pack", py("eng/build.py", "pack-existing", "--version", "{version}"),
        needs=("database.native",), resources=("dotnet",))
    add("database.business-aot", py("eng/build.py", "consumer-probes", "--version", "{version}"),
        needs=("database.pack",), resources=("pg-server",))
    add("database.compatibility", py("tests/Verification.Compatibility/verify.py", "--evidence",
        "artifacts/cpp-compatibility", "--design-root", "artifacts/scope-design"),
        needs=("database.native",), resources=("dotnet", "scope-design"))
    add("database.interop-properties", py("tests/Verification.Interop/verify.py", "--output",
        "artifacts/cpp-interop-properties"), needs=("database.native",), resources=("dotnet",))
    add("weekly.runtime", ("bash","eng/verify-task04-linux.sh"), needs=("database.native",), resources=("dotnet","qemu","pg-server","design"))
    add("weekly.transfer", ("bash","eng/verify-task07-linux.sh"), needs=("database.pack",), resources=("pg-server","design"))
    add("weekly.sanitizers", ("bash","eng/verify-cpp-sanitizers.sh"), resources=(*native,"pg-server"))
    add("weekly.clean-build", ("bash","eng/verify-cpp-clean.sh"), needs=("database.native",), resources=("scope-design","dotnet","pg-server"))
    add("weekly.migration", py("eng/build.py","compare-existing","--version","{version}"), needs=("database.pack",), resources=("pg-server",))
    for ident, directory, flag in [
        ("abstraction", "Data.Abstractions", ""), ("codec", "Data.Codecs", ""),
        ("transaction", "Data.Transactions", "--design"), ("streaming", "Data.Streaming", "--design-root"),
        ("binding", "Query.Binding", "--design")]:
        add(ident+".test", py(f"tests/{directory}/verify.py", "--output", f"artifacts/component-tests/{ident}",
            "--native-build", FULL_NATIVE, *([flag, DESIGN] if flag else [])), needs=("database.native",),
            resources=("dotnet", "pg-server", *(["design"] if flag else [])))
    for name in ("Abi", "Async", "Callbacks", "Errors", "Execution", "Lifetime", "Memory", "Negotiation"):
        key = "interop."+name.lower()
        steps[key] = Interop(key, ("dotnet","native", *(("plan","design") if name=="Negotiation" else ())), (), name)
    for target in ("abi-bootstrap","interop-corpus"):
        key = "fixture."+target
        steps[key] = Fixture(key, ("native",))
    add("interop.native", py("eng/ci/worker.py","interop-native"), resources=("native",))
    add("generator.test", ("dotnet","run","--project","tests/Query.Generator/Query.Generator.Tests.csproj","-c","Release","-p:RestoreLockedMode=true"),
        ("dotnet","run","--project","tests/Query.Generator/ApiCheck","-c","Release","-p:RestoreLockedMode=true","--","{root}",DESIGN,"{sdk}"),
        resources=("design","dotnet"))
    add("pack.api", ("dotnet","run","--project","tests/Packaging.Release/ApiCheck","-c","Release","-p:RestoreLockedMode=true","--","artifacts/packaging-api.md"), resources=("dotnet",))
    add("elf.test", py("-m","unittest","discover","-s","tests/Packaging.Linux","-p","test_profile.py","-v"), resources=("native",))
    add("database.native-tests", ("ctest","--test-dir",FULL_NATIVE,"--output-on-failure","--no-tests=error"), needs=("database.native",), resources=("pg-server",))
    for provider, directory, design in (("postgresql","Data.Postgres","--design-root"),("sqlite","Data.Sqlite","--design")):
        preset = "linux-x64-"+provider
        build = "artifacts/cmake/"+preset
        add(provider+".build", py("eng/ci/native_graph.py","query",build),
            py("eng/task07_native.py","--output", "artifacts/provider-native/"+provider,"--provider",provider),
            resources=("native", *(("pg-tools",) if provider=="postgresql" else ())))
        add(provider+".dependencies",py("eng/ci/native_graph.py","verify",build,"nw_database_"+provider),needs=(provider+".build",))
        for operation in ("test","api"):
            options = ("--postgres-bin","artifacts/postgres-tools/install/bin") if provider=="postgresql" and operation=="test" else ()
            cmd = py(f"tests/{directory}/verify.py",f"--{operation}-only",design,DESIGN,"--output",
                     f"artifacts/provider-{operation}/{provider}","--native-build",build,*options)
            ctest = ("ctest","--preset",preset,"--no-tests=error","-R",r"^native\.selected\.abi$")
            add(provider+"."+operation, *((ctest,cmd) if operation=="test" else (cmd,)),
                needs=(provider+".build",),resources=("design","dotnet",*(("pg-server",) if options else ())))
    return steps

def ordered(actions, steps):
    result, visiting = [], set()
    def visit(name):
        if name in result:
            return
        if name in visiting or name not in steps:
            raise ValueError(f"Invalid CI action or dependency: {name}")
        visiting.add(name)
        for dep in steps[name].needs:
            visit(dep)
        visiting.remove(name)
        result.append(name)
    for name in sorted(actions):
        visit(name)
    return result

class Context:
    def __init__(self, plan_path):
        self.variables = dict(root=str(Path.cwd()), sdk=os.environ.get("DOTNET_ROOT",""),
            version=os.environ.get("PACKAGE_VERSION","0.1.0-local.1"), plan=str(plan_path))

    def call(self, command, env=None):
        command = list(map(str, command))
        print("+", shlex.join(command), flush=True)
        subprocess.run(command, check=True, env=os.environ | (env or {}))

    def execute(self, steps, names):
        for index, name in enumerate(names, 1):
            start = time.monotonic()
            print(f"[{index}/{len(names)}] {name}", flush=True)
            try:
                steps[name].run(self)
            finally:
                print(f"{name}: {time.monotonic()-start:.2f}s", flush=True)
