"""Small deterministic local NuGet packer for exact, already-built component bytes.

The returned whole-package digest stays external. Native fixture packages remain
explicit conformance packages; this module does not publish or promote them.
"""
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile


def pack(destination, identity, version, entries, dependencies=None, repository=None, *, description=None, readme=None):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}", identity):
        raise ValueError("Invalid package identity")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("An exact normalized package version is required")
    package = ET.Element("package", xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd")
    metadata = ET.SubElement(package, "metadata")
    for key, value in {"id": identity, "version": version, "authors": "IzuSharp",
                       "description": description or f"{identity} component for Korda applications and tooling.",
                       "license": "MIT", "licenseUrl": "https://licenses.nuget.org/MIT"}.items():
        element = ET.SubElement(metadata, key, {"type": "expression"} if key == "license" else {})
        element.text = value
    if readme is not None:
        if readme not in entries:
            raise ValueError("Package README is missing from its payload")
        ET.SubElement(metadata, "readme").text = readme
    if repository is not None:
        url, commit = repository
        if not url.startswith("https://") or not re.fullmatch(r"[0-9a-f]{40}", commit):
            raise ValueError("Repository provenance requires an HTTPS URL and full commit")
        ET.SubElement(metadata, "repository", type="git", url=url, commit=commit)
    if dependencies:
        group = ET.SubElement(ET.SubElement(metadata, "dependencies"), "group", targetFramework="net11.0")
        for name, exact in sorted(dependencies.items()):
            ET.SubElement(group, "dependency", id=name, version=f"[{exact}]")
    files = dict(entries)
    files[identity + ".nuspec"] = ET.tostring(package, encoding="utf-8", xml_declaration=True)
    files["[Content_Types].xml"] = b'<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="nuspec" ContentType="application/octet"/></Types>'
    files["_rels/.rels"] = ('<?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/' + identity + '.nuspec" Id="R1"/></Relationships>').encode()
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    output = destination / f"{identity}.{version}.nupkg"
    if output.exists():
        raise ValueError("A package version is immutable inside its staging feed")
    temporary = output.with_suffix(".partial")
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, value in sorted(files.items()):
                if name.startswith("/") or "\\" in name or ":" in name or any(p in ("", ".", "..") for p in name.split("/")):
                    raise ValueError("Noncanonical package entry")
                info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, value.read_bytes() if isinstance(value, Path) else value)
        temporary.rename(output)
    finally:
        temporary.unlink(missing_ok=True)
    return output
