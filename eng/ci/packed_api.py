"""Check the delivered managed payload against build outputs. Stage archive bytes for Roslyn."""

import argparse
from pathlib import Path
import zipfile

from bundles import BUILD_TASK, IDS, MANAGED, OUT, TASK_PROJECT, managed_entries


def managed_outputs(provider):
    components = (set(MANAGED) - set(IDS.values())) | {IDS[provider]}
    outputs = {}
    for component in components:
        for entry, path in managed_entries(component).items():
            if entry.endswith((".dll", ".xml")):
                outputs[entry] = path
    for ext in (".dll", ".xml"):
        filename = BUILD_TASK + ext
        outputs["tools/net11.0/" + filename] = TASK_PROJECT.parent / "bin/Release/net11.0" / filename
    return outputs


def stage(package, outputs, destination):
    with zipfile.ZipFile(package) as archive:
        names = [
            name
            for name in archive.namelist()
            if name.startswith(("lib/", "tools/")) and name.lower().endswith((".dll", ".xml"))
        ]
        if len(names) != len(set(names)) or set(names) != outputs.keys():
            raise ValueError(f"{package.name}: unexpected or missing managed archive entries")
        destination.mkdir(parents=True, exist_ok=False)
        for name in names:
            data = archive.read(name)
            if data != outputs[name].read_bytes():
                raise ValueError(f"{package.name}: {name} differs from the compiled output")
            if name.startswith("lib/net11.0/"):
                (destination / Path(name).name).write_bytes(data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("provider", choices=IDS)
    parser.add_argument("version")
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    path = OUT / "feed" / f"{IDS[args.provider]}.{args.version}.nupkg"
    stage(path, managed_outputs(args.provider), args.destination)
