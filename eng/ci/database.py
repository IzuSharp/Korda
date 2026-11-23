"""Translate the database module's checks to the existing Linux build recipes."""
PROVIDERS = ("sqlite", "postgresql")
CHECKS = {"full", "deep", "pack", "task", "generator", "elf-profile", "pack-api", "public", *PROVIDERS}


def build_plan(checks, changed, pending, published, full=False):
    unknown = checks - CHECKS
    if unknown:
        raise ValueError(f"Unknown database checks: {sorted(unknown)}")
    deep = full or "deep" in checks
    providers = checks.intersection(PROVIDERS)
    pack = bool(checks.intersection({"pack", "task", "public"}))
    bootstrap = any(not published.get(p, {}).get("inputs") for p in PROVIDERS)
    if deep or "full" in checks or ((pending or "public" in checks) and bootstrap) or pending - changed or (providers and pack):
        scope = "full"
    elif providers == set(PROVIDERS):
        scope = "full" if changed or pending else "provider-tests"
    elif providers:
        scope = next(iter(providers))
    else:
        scope = "pack" if pack else "light"
    extra = checks.intersection({"generator", "elf-profile", "pack-api"})
    if scope == "full":
        extra.difference_update({"generator", "pack-api"})
    if scope == "pack":
        extra.add("pack-metadata")
        if "task" in checks:
            extra.add("pack-api")
    return dict(scope=scope, deep=deep, checks=sorted(extra), rebuild_task="task" in checks,
                build_scopes=list(PROVIDERS) if scope == "provider-tests" else [scope],
                consume=bool(pending) or full or "public" in checks or scope == "pack")
