"""Read published provider versions from the official NuGet flat container."""
import io
import json
import re
import time
import zipfile
from urllib.error import HTTPError
from urllib.request import urlopen

from impact import IDS


def read(url):
    for attempt in range(3):
        try:
            with urlopen(url, timeout=40) as response:
                return response.read()
        except OSError as error:
            if isinstance(error, HTTPError) and error.code == 404 or attempt == 2:
                raise
            time.sleep(2 ** attempt)


def package(identity, version):
    name = identity.lower()
    return read(f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg")


def public_heads():
    heads = {}
    for provider, identity in IDS.items():
        url = f"https://api.nuget.org/v3-flatcontainer/{identity.lower()}/index.json"
        versions = json.loads(read(url))["versions"]
        pattern = r"0\.1\.0-preview\.(\d+)\.(\d+)"
        releases = [v for v in versions if re.fullmatch(pattern, v)]
        version = max(releases, key=lambda v: tuple(map(int, re.fullmatch(pattern, v).groups())))
        with zipfile.ZipFile(io.BytesIO(package(identity, version))) as archive:
            info = (json.loads(archive.read("nativeweave/public.json"))
                    if "nativeweave/public.json" in archive.namelist() else {})
        heads[provider] = {"version": version, "inputs": info.get("inputs")}
    return heads
