# Verification consumer examples

B22 is an external verification application, not a new public framework package.
The minimum example runs one real SQLite busy fixture against an existing exact
reference-service executable:

```sh
python tests/Verification.EndToEnd/run.py \
  --artifact-directory /absolute/b21-jit-artifact \
  --mode jit --provider sqlite --cases sqlite-busy \
  --output /absolute/new/minimum-fault-run
```

The advanced example builds the explicit public observation profile, selects its
actual Linux x64 Native AOT output, and runs the PostgreSQL fault group with a
trusted local test certificate:

```sh
python tests/Verification.EndToEnd/prepare_observer.py \
  --reference-source /absolute/accepted/reference-consumer \
  --feed /absolute/existing/feed --output /absolute/new/observed-build
python tests/Verification.EndToEnd/run.py \
  --artifact-directory /absolute/new/observed-build/artifacts/aot \
  --mode aot --provider postgresql --observed \
  --postgres-bin /absolute/postgres-18.0/bin \
  --certificate /absolute/test/localhost.pem \
  --private-key /absolute/test/localhost.private.pem \
  --output /absolute/new/advanced-fault-run
```

These commands start real child processes and mutate their disposable databases.
They must not target user or production data. Preserve each receipt and its
private/local data boundaries. The generated application uses normal exact NuGet
PackageReferences; neither example launches Cargo. Long-stage execution and
independent release acceptance are separate, explicitly recorded gates.
