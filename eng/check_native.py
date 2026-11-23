#!/usr/bin/env python3
"""C/C++ formatting and weekly or opt-in local static analysis."""
import argparse
import os
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import re
import subprocess
from cpp_toolchain import ROOT, LOCK, sha

GENERATED = {'native/interop/abi/include/nativeweave_abi_v1.h',
             'native/database/binding/nativeweave_database_binding_v1.h',
             'tests/Interop.Abi/native/layout.c', 'tests/Interop.Abi/native/layout.cpp'}


def files():
    names = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z', '--',
                                    'native', 'tests', 'src/Verification.Interop', 'src/Verification.EndToEnd'], cwd=ROOT).decode().split('\0')
    return sorted({name for name in names if name and Path(name).suffix in ('.c','.cc','.cpp','.cxx','.h','.hh','.hpp','.hxx')
                   and name not in GENERATED and not re.search(r'(^|/)(third_party|vendor|generated)/', name)})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['format', 'tidy'])
    parser.add_argument('--build', type=Path, default=Path(os.environ.get('NW_CMAKE_BUILD_DIR', ROOT / 'artifacts/cmake/linux-x64-release')))
    parser.add_argument('--local', action='store_true', help='Use clang-tidy from PATH and the local compilation database')
    args = parser.parse_args()
    import shutil
    name = 'clang-format' if args.mode == 'format' else 'clang-tidy'
    executable = shutil.which(name)
    lock = json.loads(LOCK.read_text())
    if not executable or not args.local and sha(Path(executable).resolve()) != lock['toolchain']['llvm']['executables'][name]:
        raise RuntimeError('Use the pinned LLVM ' + lock['toolchain']['llvm']['version'] + ' ' + name)
    if args.mode == 'format':
        subprocess.run([executable, '--dry-run', '--Werror', *files()], cwd=ROOT, check=True)
    else:
        if not (args.build / 'compile_commands.json').is_file():
            raise RuntimeError('Configure a CMake preset with CMAKE_EXPORT_COMPILE_COMMANDS=ON; set NW_CMAKE_BUILD_DIR to its build directory')
        tracked = {ROOT / name for name in files()}
        commands = json.loads((args.build / 'compile_commands.json').read_text())
        selected = {}
        for row in commands:
            path = Path(row['file']).resolve()
            if path not in tracked or path.suffix not in ('.c', '.cc', '.cpp', '.cxx'):
                continue
            previous = selected.get(path)
            if previous is None or 'nativeweave_database_host.dir' in row['command']:
                selected[path] = row
        if not selected:
            raise RuntimeError('No tracked translation units found in the compilation database')
        output = ROOT / 'artifacts/native-tidy'; output.mkdir(parents=True, exist_ok=True)
        (output / 'compile_commands.json').write_text(json.dumps(list(selected.values()), indent=2))
        def check(path):
            log = output / (path.relative_to(ROOT).as_posix().replace('/', '_') + '.log')
            with log.open('w') as stream:
                result = subprocess.run([executable, '-p', str(output), str(path)], cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
            if result.returncode:
                return str(path) + ': ' + log.read_text()[-5000:]
            return None
        with ThreadPoolExecutor(max_workers=min(8, os.cpu_count() or 1)) as pool:
            failures = [value for value in pool.map(check, selected) if value]
        if failures:
            raise RuntimeError('\n'.join(failures))
    print('PASS ' + name + ' project C/C++ checks')


if __name__ == '__main__':
    main()
