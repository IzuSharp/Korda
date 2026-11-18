#!/usr/bin/env python3
"""Build only the comparator's pinned SQLite shared library; never called by a consumer project."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

EXPECTED = {'sqlite3.c': '9512509b1bccb7461f79bea8aad6280ae4699e925fa4804381b71f59e7efb0c5',
            'sqlite3.h': '19585c8b5230e9d4f223bf31b709ece7b6a0bb3faf00d8310625d8e58cda1b1d'}
FLAGS = ['SQLITE_CORE', 'SQLITE_DEFAULT_FOREIGN_KEYS=1', 'SQLITE_ENABLE_API_ARMOR',
         'SQLITE_ENABLE_COLUMN_METADATA', 'SQLITE_ENABLE_DBSTAT_VTAB', 'SQLITE_ENABLE_FTS3',
         'SQLITE_ENABLE_FTS3_PARENTHESIS', 'SQLITE_ENABLE_FTS5', 'SQLITE_ENABLE_JSON1',
         'SQLITE_ENABLE_LOAD_EXTENSION=1', 'SQLITE_ENABLE_MEMORY_MANAGEMENT', 'SQLITE_ENABLE_RTREE',
         'SQLITE_ENABLE_STAT4', 'SQLITE_SOUNDEX', 'SQLITE_THREADSAFE=1', 'SQLITE_USE_URI',
         'HAVE_USLEEP=1', 'HAVE_ISNAN', '_POSIX_THREAD_SAFE_FUNCTIONS', 'HAVE_LOCALTIME_R']

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--amalgamation', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--compiler', default='clang')
    args = parser.parse_args()
    for name, digest in EXPECTED.items():
        if sha(args.amalgamation / name) != digest:
            raise ValueError('Expected the unchanged libsqlite3-sys 0.37.0 SQLite 3.51.3 amalgamation: ' + name)
    args.output.mkdir(parents=True, exist_ok=True)
    library = args.output / 'libsqlite3.so'
    command = [args.compiler, '-O3', '-fPIC', '-shared', '-march=x86-64',
               *('-D' + flag for flag in FLAGS), str(args.amalgamation / 'sqlite3.c'), '-o', str(library), '-lm', '-ldl', '-lpthread']
    subprocess.run(command, check=True)
    (args.output / 'sqlite-build.json').write_text(json.dumps({'sqliteVersion': '3.51.3', 'sourceSha256': EXPECTED,
        'flags': FLAGS, 'command': command, 'librarySha256': sha(library),
        'scope': 'Comparator-only producer setup. Normal C# builds consume this fixed shared library.'}, indent=2) + '\n')
    print(library)

if __name__ == '__main__':
    main()
