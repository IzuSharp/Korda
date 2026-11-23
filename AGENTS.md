# Repository instructions

- Target Native AOT for product libraries. Run compiled consumers directly on the Linux runner and skip JIT consumer builds and tests. Run clean-image and CPU emulation checks only on explicit request.
- Publish only independently installable top-level packages. Bundle their internal components and native assets. Keep internal modules independently buildable and testable.
- Register each new module's source and test paths, dependencies, checks, and any package outputs. Follow the [module registration guide](eng/ci/modules/README.md).
- CI uses the committed diff and module dependencies to select checks and package candidates. Inspect the plan with `python3 eng/ci/impact.py origin/main`. If it misses affected consumers or selects unrelated work, fix and test the routing in the same PR. Run each required check once and independent checks in parallel.
- Validate affected packages on the PR. After merge, publish only verified top-level packages with changed contents.
