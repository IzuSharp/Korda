"""Render and verify the user-facing NuGet metadata for a database provider."""

from string import Template
from catalog import ROOT


def render(provider, identity, version):
    directory = ROOT / "packaging/providers"
    description = (directory / f"{provider}.txt").read_text().strip()
    if not description:
        raise ValueError(f"{provider}: empty NuGet description")
    readme = Template((directory / "README.md").read_text()).substitute(
        package=identity, version=version, description=description
    )
    return description, readme.encode()


def verify(archive, metadata, provider, identity, version):
    description, readme = render(provider, identity, version)
    if metadata.findtext("{*}description") != description:
        raise ValueError(f"{identity}: packaged NuGet description differs")
    if metadata.findtext("{*}readme") != "README.md" or archive.read("README.md") != readme:
        raise ValueError(f"{identity}: packaged README differs")
