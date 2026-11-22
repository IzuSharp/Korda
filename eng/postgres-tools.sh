#!/usr/bin/env bash
# Maintainer-only PostgreSQL 18 binaries for the retained TLS/process fault tests.
set -euo pipefail
cd "$(dirname "$0")/.."
db_scope_pg_root="${NW_POSTGRES_TOOLS_ROOT:-$PWD/artifacts/postgres-tools}"
db_cpp_product_root="$PWD"
profile=${1:-full}
case "$profile" in full|client) ;; *) exit 2 ;; esac
key=$(python3 eng/postgres-cache-key.py "$profile")
mkdir -p "$db_scope_pg_root"
exec 9>"$db_scope_pg_root/build.lock"
flock 9
export_paths() {
  if [ -n "${GITHUB_ENV:-}" ]; then
    echo "NW_POSTGRES_BIN=$db_scope_pg_root/install/bin" >> "$GITHUB_ENV"
    echo "CMAKE_PREFIX_PATH=$db_scope_pg_root/install" >> "$GITHUB_ENV"
    echo "$db_scope_pg_root/install/bin" >> "$GITHUB_PATH"
  fi
}
if [ -f "$db_scope_pg_root/install/nativeweave-postgresql-build.json" ] &&
   python3 - "$db_scope_pg_root" "$key" <<'CHECK'
import hashlib, json, sys
from pathlib import Path
root = Path(sys.argv[1])
record = json.loads((root/'install/nativeweave-postgresql-build.json').read_text())
if record.get('cacheKey') != sys.argv[2]: sys.exit(1)
for name, digest in record['files'].items():
    if name.startswith('install/'):
        with (root/name).open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != digest: sys.exit(1)
CHECK
then
  export_paths
  echo "Reusing verified PostgreSQL $profile install"
  exit 0
fi
# Rebuild in a fresh source directory, including after an interrupted compile.
source_dir=$(mktemp -d "$db_scope_pg_root/build-XXXXXX")
trap 'rm -rf -- "$source_dir"' EXIT
test -n "${CC:-}"
test -n "${CXX:-}"
python3 - "$CC" "$CXX" <<'PY'
import hashlib,json,sys
from pathlib import Path
lock=json.loads(Path('native/dependencies.lock.json').read_text())['toolchain']['llvm']['executables']
for name,value in zip(('clang','clang++'),sys.argv[1:]):
    with Path(value).resolve().open('rb') as stream:
        assert hashlib.file_digest(stream,'sha256').hexdigest()==lock[name], 'Use pinned Clang 23.1.5'
PY
if [ ! -f "$db_scope_pg_root/postgresql-18.0.tar.bz2" ]; then
curl --fail --location --retry 3 \
  https://ftp.postgresql.org/pub/source/v18.0/postgresql-18.0.tar.bz2 \
  -o "$source_dir/download"
mv "$source_dir/download" "$db_scope_pg_root/postgresql-18.0.tar.bz2"
fi
python3 - "$db_scope_pg_root/postgresql-18.0.tar.bz2" <<'PY'
import hashlib,sys
from pathlib import Path
assert hashlib.sha256(Path(sys.argv[1]).read_bytes()).hexdigest() == "0d5b903b1e5fe361bca7aa9507519933773eb34266b1357c4e7780fdee6d6078"
PY
tar -xf "$db_scope_pg_root/postgresql-18.0.tar.bz2" -C "$source_dir"
cd "$source_dir/postgresql-18.0"
patch -p1 < "$db_cpp_product_root/native/cpp/patches/libpq-explicit-configuration.patch"
./configure --prefix="$db_scope_pg_root/install" --without-icu --without-readline --with-ssl=openssl > "$db_scope_pg_root/configure.log"
jobs=${CMAKE_BUILD_PARALLEL_LEVEL:-4}
if [ "$profile" = full ]; then
  make -j"$jobs" > "$db_scope_pg_root/build.log"
  make install > "$db_scope_pg_root/install.log"
else
  {
    make -j"$jobs" -C src/backend generated-headers
    make -j"$jobs" -C src/interfaces/libpq
    make -j"$jobs" -C src/bin/pg_config
  } > "$db_scope_pg_root/build.log"
  for subdir in src/include src/common src/port src/interfaces/libpq src/bin/pg_config; do
    make -C "$subdir" install
  done > "$db_scope_pg_root/install.log"
fi
python3 - "$db_scope_pg_root" "$CC" "$db_cpp_product_root" "$key" <<'PY'
import hashlib,json,subprocess,sys
from pathlib import Path
root=Path(sys.argv[1]);compiler=Path(sys.argv[2]).resolve()
def sha(p):
    with p.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
files=[root/'postgresql-18.0.tar.bz2',root/'configure.log',root/'build.log',root/'install.log',
       *sorted(p for p in (root/'install').rglob('*') if p.is_file())]
record={'cacheKey':sys.argv[4],'version':'18.0','archiveSha256':sha(files[0]),'compiler':str(compiler),'compilerSha256':sha(compiler),
        'compilerVersion':subprocess.check_output([str(compiler),'--version'],text=True),
        'files':{str(p.relative_to(root)):sha(p) for p in files}}
record['patchSha256']=sha(Path(sys.argv[3])/'native/cpp/patches/libpq-explicit-configuration.patch')
(root/'install/nativeweave-postgresql-build.json').write_text(json.dumps(record,indent=2)+'\n')
PY
"$db_scope_pg_root/install/bin/pg_config" --version
export_paths
