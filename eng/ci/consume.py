"""Restore only the requested public IDs and execute independent and combined JIT/AOT applications."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from impact import IDS

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/Packaging.Domain"))
from consumer_cases import GRAPHS, project


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("record", type=Path)
    parser.add_argument("plan", type=Path)
    parser.add_argument("--public", action="store_true")
    parser.add_argument("--check-build-only", action="store_true", help="Cross-build linux-x64 without executing")
    args = parser.parse_args()
    record = json.loads(args.record.read_text())
    selected = {p["id"]: p["version"] for p in record["packages"]}
    versions = {IDS[p]: v["version"] for p, v in json.loads(args.plan.read_text())["published"].items()} | selected
    with tempfile.TemporaryDirectory(prefix="nativeweave-public-") as tmp:
        work = Path(tmp)
        conf = ET.parse(ROOT / "NuGet.Config")
        if not args.public:
            ET.SubElement(conf.find("packageSources"), "add", key="candidate", value=str(args.record.resolve().parent / "feed"))
            local = ET.SubElement(conf.find("packageSourceMapping"), "packageSource", key="candidate")
            for identity in selected:
                ET.SubElement(local, "package", pattern=identity)
        conf.write(work / "NuGet.Config")
        shutil.copy(ROOT / "global.json", work)
        env = os.environ | {"NUGET_PACKAGES": str(work / "cache")}
        for graph, (refs, symbols, providers) in GRAPHS.items():
            if graph != "combined" and refs[0] not in selected:
                continue
            app = work / graph
            app.mkdir()
            shutil.copy(ROOT / "samples/api/packaging-domain/Program.cs", app)
            project(app / "Consumer.csproj", refs, versions, [*symbols, "PUBLIC_PROVIDER_BUNDLE"])
            def run(*argv, extra=None):
                subprocess.run(argv, cwd=app, env=env | (extra or {}), check=True)
            run("dotnet", "build", "-c", "Release", "-r", "linux-x64", "--no-self-contained")
            assets = json.loads((app / "obj/project.assets.json").read_text())
            actual = {key for key in assets["libraries"] if key.lower().startswith("nativeweave.")}
            if actual != {name + "/" + versions[name] for name in refs}:
                raise ValueError("Unexpected NativeWeave dependencies: " + str(actual))
            def execute(*command):
                for provider in providers:
                    database = str(app / "smoke.db") if provider == "sqlite" else os.environ["NW_TEST_POSTGRES"]
                    run(*command, extra={"B24_PROVIDER": provider, "B24_DATABASE": database})

            if not args.check_build_only:
                execute("dotnet", "bin/Release/net11.0/linux-x64/B24Consumer.dll", "--require-jit")
            options = ["--no-self-contained"] if args.check_build_only else ["-p:PublishAot=true"]
            run("dotnet", "publish", "-c", "Release", "-r", "linux-x64", *options, "-o", "out")
            if not args.check_build_only:
                execute("./out/B24Consumer", "--require-aot")


if __name__ == "__main__":
    main()
