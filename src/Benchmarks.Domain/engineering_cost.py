"""Recorded deployment/source/build costs; no new benchmark timing or builds."""
from collections import Counter
from pathlib import Path
import re

from common import check_files, digest, load


def source_lines(files):
    """Count physical lines by role, retaining the exact file and content hash."""
    rows = []
    for name, expected in sorted(files.items()):
        path = Path(name)
        if path.suffix not in ('.cs', '.rs', '.sql', '.py'):
            continue
        if digest(path) != expected:
            raise ValueError('Source cost inventory changed: ' + name)
        role = ('generated-output' if 'obj' in path.parts else 'sql-schema' if path.suffix == '.sql'
                else 'rust-comparator' if path.suffix == '.rs' else 'managed-application' if path.suffix == '.cs'
                else 'external-verification-tool')
        rows.append({'path': str(path), 'sha256': expected, 'role': role,
                     'physical_lines': len(path.read_text().splitlines()), 'bytes': path.stat().st_size})
    totals = Counter()
    for row in rows:
        totals[row['role']] += row['physical_lines']
    return {'files': rows, 'physical_lines_by_role': dict(totals),
            'method': 'Physical lines including comments/blanks, not a complexity or productivity score. Source roles are not summed across unrelated framework layers.'}


def command_costs(root, commands):
    rows = []
    for command in commands:
        if not any(word in command.get('label', '') for word in ('restore', 'build', 'publish')):
            continue
        log = root / command['log']
        expected = command.get('sha256', command.get('log_sha256'))
        if expected and digest(log) != expected:
            raise ValueError('Recorded build log changed: ' + str(log))
        match = re.search(r'Time Elapsed (\d+):(\d+):(\d+(?:\.\d+)?)', log.read_text(errors='replace'))
        duration = command.get('elapsed_ns')
        reported = (int(match[1]) * 3600 + int(match[2]) * 60 + float(match[3])) if match else None
        rows.append({'label': command['label'], 'argv': command['argv'], 'exit_code': command['exit_code'],
            'elapsed_seconds': duration / 1e9 if duration is not None else reported,
            'timing_method': 'external monotonic command duration' if duration is not None else
                'MSBuild-reported Time Elapsed' if reported is not None else 'not recorded',
            'log': str(log), 'sha256': digest(log),
            'scope': 'Recorded developer build with available caches; not an isolated cold-machine build or runtime performance measurement'})
    return rows


def application_inventory(probe):
    progress = load(probe / 'progress.json')
    variants = {}
    for name, pins in progress['consumer_sources'].items():
        directory = probe / 'consumers' / name
        variants[name] = source_lines({str(directory / file): value for file, value in pins.items()})
    source_deltas = []
    baseline = progress['consumer_sources'].get('baseline', {})
    for name, pins in progress['consumer_sources'].items():
        if name == 'baseline':
            continue
        changed = sorted(file for file in set(baseline) | set(pins) if baseline.get(file) != pins.get(file))
        builds = [build for build in progress['builds'].values() if build.get('variant') == name]
        unchanged = bool(builds) and all(value['sha256'] == progress['inputs']['native_sha256'][file]
            for build in builds for file, value in build['native'].items())
        source_deltas.append({'variant': name, 'changed_files': changed, 'native_modules_unchanged': unchanged,
            'no_consumer_rust_invocation': progress.get('no_consumer_rust_invocation'),
            'builds': [key for key, value in progress['builds'].items() if value.get('variant') == name]})
    generated = probe / 'consumers/baseline/obj/Release'
    generated_files = {str(file): digest(file) for file in generated.rglob('*.cs')} if generated.exists() else {}
    return {'receipt': {'path': str(probe / 'progress.json'), 'sha256': digest(probe / 'progress.json')},
        'application_variants': variants, 'ordinary_edit_deltas': source_deltas,
        'recorded_build_commands': command_costs(probe, progress['commands']),
        'packages': progress['inputs']['packages'],
        'compressed_nativeweave_package_bytes': sum(value['bytes'] for value in progress['inputs']['packages'].values()),
        'persisted_generated_source': source_lines(generated_files),
        'generated_scope': 'Only persisted compiler output is counted. Source-generator output not emitted to disk is unavailable, not zero. Generator package/lock identities remain in the closure.',
        'reuse': 'Accepted B21 consumer/edit evidence is reused; this inventory does not re-run its acceptance.'}


def inventory(routes, edit_probe=None, instrumented=None):
    result = {'routes': {}, 'builds': [], 'ordinary_edit': None,
        'method': 'Content-verified physical file sizes and recorded command evidence; no inferred allocation/FFI/copy or productivity score',
        'optional_protocol_closure': 'The shipped NativeWeave Server module contains HTTP/TLS/Tonic/rmcp native code. Unconfigured managed protocol facades do not remove that code. Both provider assets are present in the full reference consumer; loaded mappings are observed separately.',
        'unavailable': ['Time from an empty user project to a useful call was not independently timed',
                        'Custom native/framework engineering effort is not inferred from application line counts',
                        'Unpersisted generated-source lines and comparator build durations without receipts are unavailable']}
    for name, route in routes.items():
        check_files(route['artifacts'])
        files = []
        for file, expected in sorted(route['artifacts'].items()):
            path = Path(file)
            role = ('debug-symbol' if path.suffix in ('.dbg', '.pdb') else 'xml-documentation' if path.suffix == '.xml'
                    else 'native-module' if path.suffix == '.so' else 'runtime-executable' if file == route['command'][0]
                    else 'runtime-metadata')
            files.append({'path': file, 'sha256': expected, 'bytes': path.stat().st_size, 'role': role})
        totals = Counter()
        for file in files:
            totals[file['role']] += file['bytes']
        row = {'files': files, 'bytes_by_role': dict(totals),
            'runtime_deployment_bytes': sum(file['bytes'] for file in files if file['role'] not in ('debug-symbol', 'xml-documentation')),
            'limitations': route.get('limitations', []), 'toolchain': route.get('toolchain'), 'lock_hashes': route['lock_hashes']}
        if route.get('package_probe_receipt'):
            reference = route['package_probe_receipt']
            if digest(reference['path']) != reference['sha256']:
                raise ValueError('Accepted package receipt changed')
            row['application'] = application_inventory(Path(reference['path']).parent)
        else:
            source = route.get('source', {})
            base = Path(source.get('repository', '.'))
            row['application'] = source_lines({str(base / path): value for path, value in source.get('files', {}).items()})
            row['recorded_build_receipts'] = route.get('build_receipts', route.get('build_commands'))
            reference = route.get('build_receipts')
            if reference:
                if digest(reference['path']) != reference['sha256']:
                    raise ValueError('Comparator build receipt changed')
                commands = []
                for build in load(reference['path'])['builds']:
                    if build['route'] != name:
                        continue
                    if build.get('log') and digest(build['log']) != build['log_sha256']:
                        raise ValueError('Comparator build log changed')
                    external = build.get('elapsed_seconds')
                    reported = build.get('compiler_reported_elapsed_seconds')
                    commands.append({'label': build.get('purpose', 'recorded producer build'),
                        'argv': build.get('command'), 'elapsed_seconds': external if external is not None else reported,
                        'timing_method': 'external monotonic command duration' if external is not None else
                            'compiler-reported duration' if reported is not None else 'not recorded',
                        'limitations': build.get('timing_limitation'), 'log': build.get('log'),
                        'log_sha256': build.get('log_sha256')})
                row['recorded_build_commands'] = commands
        result['routes'][name] = row
    if edit_probe:
        result['ordinary_edit'] = application_inventory(Path(edit_probe).resolve())
    if instrumented:
        path = Path(instrumented).resolve()
        value = load(path)
        result['instrumented_build'] = {'receipt': {'path': str(path), 'sha256': digest(path)},
            'instrumentation_sha256': value['instrumentation_sha256'],
            'commands': command_costs(path.parent, value['commands']),
            'configuration_note': value['configuration_note'], 'native_hashes': {
                name: build['native_sha256'] for name, build in value['builds'].items()}}
    return result
