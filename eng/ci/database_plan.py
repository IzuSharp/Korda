"""Choose registered actions and their shared prerequisites before a runner installs tools."""

from runner import Command, ordered
from modules.database import PROVIDERS, py, recipes
from language import purpose


def catalog_steps(scope):
    steps = recipes()
    if scope not in PROVIDERS:
        return steps
    steps["public.pack"] = Command(
        "public.pack",
        ("dotnet",),
        (scope + ".build",),
        (
            py(
                "eng/ci/bundles.py",
                "--plan",
                "{plan}",
                "--version",
                "{version}",
                "--provider",
                scope,
                "--native",
                f"artifacts/provider-native/{scope}/manifest.json",
            ),
        ),
    )
    steps["public.consume-aot"] = Command(
        "public.consume-aot",
        ("pg-server",) if scope == "postgresql" else (),
        ("public.pack",),
        (py("eng/ci/consume.py", "artifacts/public/release.json", "{plan}"),),
    )
    steps["public.validate"] = Command(
        "public.validate",
        (),
        ("public.consume-aot",),
        (
            py(
                "eng/ci/validate_public.py",
                "artifacts/public",
                "{plan}",
                "{version}",
                "--module",
                scope,
            ),
        ),
    )
    return steps


def plan_actions(catalog, reasons, direct, touched, package_checks, *, previous, entries, full, images=False):
    desired = set()
    deferred = set()
    tested_public = set(package_checks)
    for module in catalog.modules if full else direct:
        actions = set(catalog.modules[module].get("checks", ()))
        paths = reasons.get(module, ())
        roles = {purpose(path, (catalog if path in entries else previous).owner(path)[1]) for path in paths}
        roles.discard("documentation")
        if not full and roles == {"api"}:
            actions = set(catalog.modules[module].get("api_checks", actions))
        if module in PROVIDERS and not full and roles == {"tests"}:
            if not any(p.endswith("/verify.py") for p in paths):
                actions.discard(module + ".api")
        desired.update(actions)
    if (
        len(touched) == 2
        and "database-native" in catalog.modules
        and direct & catalog.dependencies("database-native")
    ):
        desired.update(("database.regressions", "database.dependencies"))
    if "consumer.check" in desired or "bundle.check" in desired:
        desired.difference_update({"consumer.check", "bundle.check"})
        # Publishing code has local archive contract tests; exercise a checked candidate on the host as well.
        tested_public.update(set(PROVIDERS) & catalog.packages.keys())
    for provider in touched:
        desired.update({provider + ".dependencies", provider + ".api", provider + ".test"})
    if full:
        tested_public.update(PROVIDERS)
        desired.update(
            {
                "weekly.sanitizers",
                "image.runtime",
                "image.clean-build",
                "image.transfer",
                "manual.migration",
            }
        )
    if not full:
        deferred = {a for a in desired if a.startswith("weekly.")}
        desired -= deferred
    if not (full and images):
        image_actions = {a for a in desired if a.startswith("image.")}
        deferred.update(image_actions)
        desired -= image_actions
    if "database.regressions" in desired:
        desired -= {
            n.lower() + ".test"
            for n in (
                "Data.NativeHost",
                "Data.Migrations",
                "Data.Materialization",
                "Query.Fluent",
                "Orm.Relations",
            )
        }
        # This driver invokes these same test programs on the same host and provider libraries.
        desired -= {
            "database.native-tests",
            "abstraction.test",
            "transaction.test",
            "codec.test",
            "streaming.test",
            "binding.test",
            "interop.execution",
            "interop.negotiation",
        }
        for provider in PROVIDERS:
            desired.update((provider + ".test", provider + ".api"))
    manual = {name for name in desired if name.startswith("manual.")}
    desired -= manual
    deferred |= manual
    lanes = {}
    for name in sorted(desired):
        prefix = name.partition(".")[0]
        scope = {"fixture": "fixtures", "interop": "interop"}.get(
            prefix, prefix if prefix in PROVIDERS else "linux"
        )
        lanes.setdefault(scope, set()).add(name)
    for provider in tested_public:
        lanes.setdefault(provider, set()).add("public.validate")

    groups, resources = {}, {}
    for scope, selected in sorted(lanes.items()):
        steps = catalog_steps(scope)
        groups[scope] = ordered(selected, steps)
        resources[scope] = sorted({r for name in groups[scope] for r in steps[name].resources})
    return dict(
        scope="linux",
        deep=full,
        build_scopes=list(groups),
        actions=groups,
        resources=resources,
        public_checks=sorted(tested_public),
        deferred=sorted(deferred),
    )
