"""Run a checked plan on this host. All product executions use the normal build tools."""

import argparse
from contextlib import contextmanager
import json
import os
import socket
from pathlib import Path
import sys
import tempfile

from catalog import ROOT, git, load_catalog
from database_plan import catalog_steps
from facts import files
from runner import Context
from modules.database import recipes


def check_index():
    tree = git("write-tree").decode().strip()
    graph = load_catalog(tree)
    allowed = set(recipes()) | {"bundle.check", "consumer.check"}
    for name, spec in graph.modules.items():
        unknown = (set(spec.get("checks", ())) | set(spec.get("api_checks", ()))) - allowed
        if unknown:
            raise ValueError(f"{name}: unknown checks {sorted(unknown)}")
    for path in files(tree):
        graph.owner(path)


@contextmanager
def postgres(ctx, required):
    if not required or os.environ.get("NW_TEST_POSTGRES"):
        yield
        return
    binary = Path(os.environ.get("NW_POSTGRES_BIN", "artifacts/postgres-tools/install/bin")).resolve()
    with tempfile.TemporaryDirectory(prefix="nw-pg-") as temp:
        data = Path(temp) / "data"
        ctx.call(
            [
                binary / "initdb",
                "-D",
                data,
                "-U",
                "nativeweave",
                "-A",
                "trust",
                "--no-locale",
            ]
        )
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        ctx.call(
            [
                binary / "pg_ctl",
                "-D",
                data,
                "-l",
                Path(temp) / "postgres.log",
                "-o",
                f"-k {temp} -h 127.0.0.1 -p {port}",
                "-w",
                "start",
            ]
        )
        url = f"postgresql://nativeweave@127.0.0.1:{port}/postgres"
        keys = ("NW_TEST_POSTGRES", "NATIVEWEAVE_TEST_POSTGRES_URL")
        old = {k: os.environ.get(k) for k in keys}
        os.environ.update(dict.fromkeys(keys, url))
        try:
            yield
        finally:
            ctx.call([binary / "pg_ctl", "-D", data, "-m", "fast", "-w", "stop"])
            for key, value in old.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("check-index")
    commands.add_parser("interop-native")
    for cmd in ("run", "module-check", "module-pack"):
        sub = commands.add_parser(cmd)
        sub.add_argument("plan", type=Path)
        sub.add_argument("group")
    args = parser.parse_args()
    if args.command == "check-index":
        check_index()
        return
    if args.command == "interop-native":
        import subprocess

        os.environ["NW_CMAKE_PRESET"] = "interop"
        sys.path.insert(0, str(ROOT / "tests"))
        from native_build import prepare

        def run(label, argv):
            print(label, flush=True)
            return subprocess.check_output(argv, text=True)

        prepare(run)
        return
    plan = json.loads(args.plan.read_text())
    ctx = Context(args.plan.resolve())
    if args.command in ("module-check", "module-pack"):
        with postgres(ctx, "pg-server" in plan["module_resources"][args.group]):
            if args.command == "module-pack":
                version = os.environ.get("NW_VERSION", ctx.variables["version"] + ".2")
                ctx.call([sys.executable, "eng/ci/module_job.py", "pack", args.group, args.plan, version])
            else:
                field = plan["run_steps"][args.group]
                if field not in ("run", "integration"):
                    raise ValueError(f"Unexpected check {field}")
                ctx.call(load_catalog().modules[args.group][field], env={"NW_MODULE": args.group})
        return
    names = plan["actions"][args.group]
    steps = catalog_steps(args.group, plan["public_checks"], bool(plan["packages"]), plan["grouped_pack"])
    if len(set(names)) != len(names) or any(
        dep not in names[:i] for i, name in enumerate(names) for dep in steps[name].needs
    ):
        raise ValueError("Plan is missing a prerequisite")
    with postgres(ctx, "pg-server" in plan["resources"][args.group]):
        with tempfile.TemporaryDirectory(prefix="nw-native-check-") as session:
            os.environ["NW_NATIVE_CHECK_SESSION"] = session
            try:
                ctx.execute(steps, names)
            finally:
                os.environ.pop("NW_NATIVE_CHECK_SESSION", None)


if __name__ == "__main__":
    main()
