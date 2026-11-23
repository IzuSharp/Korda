"""Source language and purpose are independent of module ownership."""

from pathlib import PurePosixPath

EXTENSIONS = {
    "csharp": (".cs", ".csproj", ".props", ".targets"),
    "cpp": (".cpp", ".cc", ".cxx", ".hpp", ".hxx", ".hh"),
    "c": (".c",),
    "c-header": (".h",),
    "go": (".go",),
    "python": (".py", ".pyi"),
    "cmake": (".cmake",),
    "shell": (".sh",),
    "markdown": (".md",),
}


def language(path):
    file = PurePosixPath(path)
    if file.name in {"CMakeLists.txt", "CMakePresets.json"}:
        return "cmake"
    return next(
        (name for name, suffixes in EXTENSIONS.items() if file.suffix in suffixes),
        "data",
    )


def purpose(path, registered):
    file = PurePosixPath(path)
    if file.name in {"LICENSE", "THIRD_PARTY_NOTICES.md"}:
        return registered
    if "/reference/" in path and file.suffix in {".md", ".json"}:
        return "api"
    if "/ApiCheck/" in path or file.name in {
        "api-reference.md",
        "public-api.expected.json",
        "public-api-standard.json",
        "api.py",
    }:
        return "api"
    if file.suffix == ".md":
        return "documentation"
    return registered
