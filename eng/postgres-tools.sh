#!/usr/bin/env bash
# Maintainer-only PostgreSQL 18 binaries for the retained TLS/process fault tests.
set -euo pipefail
cd "$(dirname "$0")/.."
db_scope_pg_root="${NW_POSTGRES_TOOLS_ROOT:-$PWD/artifacts/postgres-tools}"
db_cpp_product_root="$PWD"
test ! -e "$db_scope_pg_root"
mkdir -p "$db_scope_pg_root"
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
curl --fail --location --retry 3 \
  https://ftp.postgresql.org/pub/source/v18.0/postgresql-18.0.tar.bz2 \
  -o "$db_scope_pg_root/postgresql-18.0.tar.bz2"
python3 - "$db_scope_pg_root/postgresql-18.0.tar.bz2" <<'PY'
import hashlib,sys
from pathlib import Path
assert hashlib.sha256(Path(sys.argv[1]).read_bytes()).hexdigest() == "0d5b903b1e5fe361bca7aa9507519933773eb34266b1357c4e7780fdee6d6078"
PY
tar -xf "$db_scope_pg_root/postgresql-18.0.tar.bz2" -C "$db_scope_pg_root"
cd "$db_scope_pg_root/postgresql-18.0"
patch -p1 < "$db_cpp_product_root/native/cpp/patches/libpq-explicit-configuration.patch"
./configure --prefix="$db_scope_pg_root/install" --without-icu --without-readline --with-ssl=openssl > "$db_scope_pg_root/configure.log"
make -j2 > "$db_scope_pg_root/build.log"
make install > "$db_scope_pg_root/install.log"
python3 - "$db_scope_pg_root" "$CC" "$db_cpp_product_root" <<'PY'
import hashlib,json,subprocess,sys
from pathlib import Path
root=Path(sys.argv[1]);compiler=Path(sys.argv[2]).resolve()
def sha(p):
    with p.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
files=[root/'postgresql-18.0.tar.bz2',root/'configure.log',root/'build.log',root/'install.log',
       *sorted((root/'install/lib').glob('*.a'))]
record={'version':'18.0','archiveSha256':sha(files[0]),'compiler':str(compiler),'compilerSha256':sha(compiler),
        'compilerVersion':subprocess.check_output([str(compiler),'--version'],text=True),
        'files':{str(p.relative_to(root)):sha(p) for p in files}}
record['patchSha256']=sha(Path(sys.argv[3])/'native/cpp/patches/libpq-explicit-configuration.patch')
(root/'install/nativeweave-postgresql-build.json').write_text(json.dumps(record,indent=2)+'\n')
PY
"$db_scope_pg_root/install/bin/postgres" --version
if [ -n "${GITHUB_ENV:-}" ]; then
  echo "NW_POSTGRES_BIN=$db_scope_pg_root/install/bin" >> "$GITHUB_ENV"
  echo "CMAKE_PREFIX_PATH=$db_scope_pg_root/install" >> "$GITHUB_ENV"
  echo "$db_scope_pg_root/install/bin" >> "$GITHUB_PATH"
fi
