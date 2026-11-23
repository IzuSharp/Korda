"""Choose registered actions and their shared prerequisites before a runner installs tools."""

from runner import Command, ordered
from modules.database import PROVIDERS, py, recipes
from language import purpose


def catalog_steps(scope, public=(), validate=False, grouped=False):
    steps = recipes()
    if not public:
        return steps
    single = len(public) == 1
    selected = public[0] if single else None
    if single and not grouped:
        args = ("--native", f"artifacts/provider-native/{selected}/manifest.json")
        prerequisite = selected + ".build"
    else:
        args = ("--components", "artifacts/db-scope/prepared/candidate")
        prerequisite = "database.pack"
    steps["public.pack"] = Command(
        "public.pack",
        ("dotnet",),
        (prerequisite,),
        (
            py(
                "eng/ci/bundles.py",
                "--plan",
                "{plan}",
                "--version",
                "{version}.2",
                *args,
            ),
        ),
    )
    steps["public.consume-aot"] = Command(
        "public.consume-aot",
        ("pg-server",) if "postgresql" in public else (),
        ("public.pack",),
        (py("eng/ci/consume.py", "artifacts/public/release.json", "{plan}"),),
    )
    if validate:
        steps["public.validate"] = Command(
            "public.validate",
            (),
            ("public.consume-aot",),
            (
                py(
                    "eng/ci/validate_public.py",
                    "artifacts/public",
                    "{plan}",
                    "{version}.2",
                ),
            ),
        )
    return steps


def plan_actions(catalog, reasons, direct, touched, pending, *, previous, entries, full):
    desired = set()
    deferred = set()
    tested_public = set(pending)
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
        tested_public.update(pending or PROVIDERS)
    for provider in touched:
        desired.update({provider + ".dependencies", provider + ".api", provider + ".test"})
    if full:
        tested_public.update(PROVIDERS)
        desired.update(
            {
                "weekly.sanitizers",
                "weekly.runtime",
                "weekly.clean-build",
                "weekly.transfer",
                "manual.migration",
            }
        )
    else:
        deferred = {a for a in desired if a.startswith("weekly.")}
        desired -= deferred
    if len(tested_public) > 1 or "database.regressions" in desired:
        if any(p + ".dependencies" in desired for p in PROVIDERS):
            desired -= {p + ".dependencies" for p in PROVIDERS}
            desired.add("database.dependencies")
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
            *(p + ".test" for p in PROVIDERS),
            *(p + ".api" for p in PROVIDERS),
        }
    manual = {name for name in desired if name.startswith("manual.")}
    desired -= manual
    deferred |= manual
    public = sorted(tested_public)
    # Validation must describe exactly the candidates present in the outgoing feed.
    if pending:
        public = sorted(pending)
    grouped = len(public) > 1 or ("database.pack" in desired or "database.business-aot" in desired)
    if public:
        desired.add("public.validate" if pending else "public.consume-aot")
    steps = catalog_steps("linux", public, bool(pending), grouped)
    actions = ordered(desired, steps)
    # A single build directory and package feed stay in one DAG. Independent fixture checks can run alongside it.
    groups = {}
    separate_providers = not public and all(p + ".build" in actions for p in PROVIDERS)
    for name in actions:
        prefix = name.partition(".")[0]
        if prefix == "fixture":
            lane = "fixtures"
        elif prefix == "interop" or (separate_providers and prefix in PROVIDERS):
            lane = prefix
        else:
            lane = "linux"
        groups.setdefault(lane, []).append(name)
    resources = {
        group: sorted({resource for name in names for resource in steps[name].resources})
        for group, names in groups.items()
    }
    return dict(
        scope=public[0] if len(public) == 1 else "linux",
        deep=full,
        rebuild_task=False,
        build_scopes=list(groups),
        actions=groups,
        resources=resources,
        public_checks=public,
        grouped_pack=grouped,
        deferred=sorted(deferred),
    )
