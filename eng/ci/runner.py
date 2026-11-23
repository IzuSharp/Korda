"""Execute module actions in dependency order, with per-step timing and ordinary process exit codes."""

from dataclasses import dataclass
from pathlib import Path
import os
import shlex
import subprocess
import time


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
        self.variables = dict(
            root=str(Path.cwd()),
            sdk=os.environ.get("DOTNET_ROOT", ""),
            version=os.environ.get("PACKAGE_VERSION", "0.1.0-local.1"),
            plan=str(plan_path),
        )

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
                print(f"{name}: {time.monotonic() - start:.2f}s", flush=True)
