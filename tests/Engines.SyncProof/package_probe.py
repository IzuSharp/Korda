"""Independent actual A17 package consumer, reusing A14/A16 packaging machinery."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import shutil
import tempfile
import xml.etree.ElementTree as ET


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def consume(root, output, native, packaging_root, run, environment, prepare):
    pack_source = packaging_root / 'src/Packaging.Linux/packages.py'
    task_project = packaging_root / 'src/Packaging.Release/BuildTasks/NativeWeave.Packaging.Tasks.csproj'
    targets = packaging_root / 'src/Packaging.Release/buildTransitive/NativeWeave.Packaging.targets'
    if not all(path.is_file() for path in [pack_source, task_project, targets]):
        raise RuntimeError('Provide --packaging-root with the A14/A16 reusable packaging component.')
    packaging_inputs = [pack_source, targets, task_project, *task_project.parent.glob('*.cs'),
        task_project.parent / 'packages.lock.json', packaging_root / 'Directory.Build.props',
        packaging_root / 'Directory.Build.targets', packaging_root / 'global.json',
        packaging_root / 'src/Build.Integration/NativeWeave.Build.targets']
    packaging_before = {str(p.relative_to(packaging_root)): digest(p) for p in packaging_inputs if p.is_file()}
    packaging_commit = run('packaging-source-commit', ['git', 'rev-parse', 'HEAD'], cwd=packaging_root).strip()
    packaging_dirty = bool(run('packaging-source-dirty', ['git', 'status', '--porcelain', '--',
        'src/Packaging.Release', 'src/Packaging.Linux'], cwd=packaging_root).strip())
    if not prepare and packaging_dirty: raise RuntimeError('Formal package evidence requires committed A14/A16 packaging input.')
    # Read another agent's worktree only. Build the exact copied source identities in
    # this verifier's artifact tree so parallel tasks never share bin/obj outputs.
    packaging_copy = output / 'packaging-source'
    for relative in packaging_before:
        destination = packaging_copy / relative; destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(packaging_root / relative, destination)
    pack_source = packaging_copy / pack_source.relative_to(packaging_root)
    task_project = packaging_copy / task_project.relative_to(packaging_root)
    targets = packaging_copy / targets.relative_to(packaging_root)
    common = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    run('packaging-task-restore', ['dotnet', 'restore', task_project, '--locked-mode', *common], cwd=packaging_copy)
    run('packaging-task-build', ['dotnet', 'build', task_project, '-c', 'Release', '--no-restore', *common], cwd=packaging_copy)
    task_dir = task_project.parent / 'bin/Release/net11.0'
    spec = importlib.util.spec_from_file_location('a17_actual_component_pack', pack_source)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    feed = output / 'packages'; feed.mkdir()
    identity = 'NativeWeave.Engines.SyncProof'; version = '0.1.0-proof.1'
    core_entries = {'LICENSE': root / 'LICENSE'}
    for folder, assembly in [('src/NativeWeave.Core', 'NativeWeave.Core'), ('src/Interop.Abi', 'NativeWeave.Interop.Abi')]:
        for suffix in ['.dll', '.xml']:
            core_entries['lib/net11.0/' + assembly + suffix] = root / folder / 'bin/Release/net11.0' / (assembly + suffix)
    core = module.pack(feed, 'NativeWeave.Core', version, core_entries)
    deps = {'NativeWeave.Core': version}
    native_path = 'runtimes/linux-x64/native/' + native.name
    asset = dict(engineId='nativeweave.engines.syncproof', facadePackageId=identity, facadePackageVersion=version,
        rid='linux-x64', sourcePath=native_path, sha256=digest(native), abiMajor=1, abiMinor=0,
        capabilities={'codec.zlib.v1': 0}, cpuBaseline='x86-64')
    closure = {'schemaVersion': 1, 'packages': [
        {'id': 'NativeWeave.Core', 'version': version, 'dependencies': {}},
        {'id': identity, 'version': version, 'dependencies': deps, 'nativeAssets': [asset]}]}
    source = {'sourceCommit': run('package-source-commit', ['git', 'rev-parse', 'HEAD']).strip(),
        'nativeSha256': digest(native), 'cargoLockSha256': digest(root / 'Cargo.lock'),
        'bindingSha256': digest(root / 'native/engines/sync-proof/binding.json'),
        'upstream': [{'crate': 'miniz_oxide', 'version': '0.9.1', 'features': ['with-alloc']}, {'crate': 'adler2', 'version': '2.0.1'}]}
    entries = {'LICENSE': root / 'LICENSE', 'THIRD-PARTY-NOTICES.txt': root / 'src/Engines.SyncProof/THIRD-PARTY-NOTICES.txt',
        native_path: native, 'nativeweave/closure.json': json.dumps(closure, sort_keys=True).encode(),
        'nativeweave/source.json': json.dumps(source, sort_keys=True).encode(),
        'buildTransitive/' + identity + '.targets': targets}
    for suffix in ['.dll', '.xml']:
        entries['tools/net11.0/NativeWeave.Packaging.Tasks' + suffix] = task_dir / ('NativeWeave.Packaging.Tasks' + suffix)
        entries['lib/net11.0/' + identity + suffix] = root / 'src/Engines.SyncProof/bin/Release/net11.0' / (identity + suffix)
    facade = module.pack(feed, identity, version, entries, deps)
    scratch = Path(tempfile.mkdtemp(prefix='nativeweave-a17-consumer-'))
    env = environment.copy(); env['NUGET_PACKAGES'] = str(scratch / 'packages')
    traps = scratch / 'rust-traps'; traps.mkdir()
    marker = scratch / 'unexpected-rust.txt'
    for tool in ['cargo', 'rustc', 'rustup']:
        path = traps / tool
        path.write_text("#!/bin/sh\nprintf 'unexpected Rust invocation\\n' >> " + shlex.quote(str(marker)) + '\nexit 197\n')
        path.chmod(0o755)
    env['PATH'] = str(traps) + os.pathsep + env['PATH']
    config = ET.Element('configuration'); sources = ET.SubElement(config, 'packageSources'); ET.SubElement(sources, 'clear')
    ET.SubElement(sources, 'add', key='candidate', value=str(feed))
    ET.SubElement(sources, 'add', key='nuget.org', value='https://api.nuget.org/v3/index.json')
    ET.ElementTree(config).write(scratch / 'NuGet.Config')
    shutil.copy2(root / 'global.json', scratch)
    project = scratch / 'Consumer.csproj'
    project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion>
<AssemblyName>NativeWeave.Engines.SyncProof.Tests</AssemblyName><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks><IsAotCompatible>true</IsAotCompatible><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetAudit>true</NuGetAudit>
<NuGetLockFilePath Condition="'$(PublishAot)' == 'true' and '$(RuntimeIdentifier)' != ''">$(MSBuildProjectDirectory)/packages.$(RuntimeIdentifier).aot.lock.json</NuGetLockFilePath>
<NuGetLockFilePath Condition="'$(PublishAot)' == 'true' and '$(RuntimeIdentifiers)' != ''">$(MSBuildProjectDirectory)/packages.$(RuntimeIdentifiers).aot.lock.json</NuGetLockFilePath>
</PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Engines.SyncProof" Version="[0.1.0-proof.1]" /></ItemGroup></Project>''')
    source_text = (root / 'tests/Engines.SyncProof/Program.cs').read_text()
    (scratch / 'Program.cs').write_text(source_text)
    run('package-consumer-restore', ['dotnet', 'restore', project, *common], cwd=scratch, environment=env)
    run('package-consumer-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common], cwd=scratch, environment=env)
    dll = scratch / 'bin/Release/net11.0/NativeWeave.Engines.SyncProof.Tests.dll'
    run('package-consumer-jit', ['dotnet', dll], cwd=scratch, environment=env)
    record_name = 'NativeWeave.nativeweave.engines.syncproof.assets.json'
    build_record = json.loads((dll.parent / record_name).read_text())
    build_native = dll.parent / build_record['outputPath']
    assert digest(build_native) == digest(native)
    assert not (dll.parent / 'NativeWeave.Packaging.Tasks.dll').exists(), 'Build tooling leaked into application runtime.'
    # Genuine consumer contract negatives: invalid null options and escaping a scoped span.
    for label, source_text_negative, diagnostic in [
        ('negative-null-limit', 'using NativeWeave.Engines.SyncProof; _ = new ZlibCodec(rawInputLimit: null);', 'CS1503'),
        ('negative-span-escape', 'using NativeWeave.Engines.SyncProof; using var b = new ZlibCodec().CompressOwned("x"u8); var escaped = b.Read<System.ReadOnlySpan<byte>>(static bytes => bytes);', 'CS9244')]:
        (scratch / 'Program.cs').write_text(source_text_negative)
        run(label, ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common], cwd=scratch, environment=env, expected=1, contains=diagnostic)
    (scratch / 'Program.cs').write_text(source_text + '\nif (!new ZlibCodec().Decompress(new ZlibCodec().Compress("managed application change"u8)).AsSpan().SequenceEqual("managed application change"u8)) throw new Exception("Application change failed");\n')
    run('package-managed-change', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *common], cwd=scratch, environment=env)
    run('package-managed-change-jit', ['dotnet', dll], cwd=scratch, environment=env)
    aot = output / 'package-aot'
    run('package-aot-restore', ['dotnet', 'restore', project, '-r', 'linux-x64', '-p:PublishAot=true', *common], cwd=scratch, environment=env)
    run('package-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:IlcTreatWarningsAsErrors=true', '--no-restore', '-o', aot, *common], cwd=scratch, environment=env)
    executable = aot / 'NativeWeave.Engines.SyncProof.Tests'
    run('package-aot-execute', [executable, '--require-aot'], cwd=scratch, environment=env)
    assert not (aot / 'NativeWeave.Engines.SyncProof.Tests.dll').exists()
    publish_record = json.loads((aot / record_name).read_text())
    assert digest(aot / publish_record['outputPath']) == digest(native) == digest(build_native)
    assert not marker.exists(), 'An application build tried to invoke Rust.'
    snapshots = output / 'independent-consumer'; snapshots.mkdir()
    for path in scratch.glob('*'):
        if path.is_file() and path.suffix in {'.cs', '.csproj', '.json', '.Config'}: shutil.copy2(path, snapshots / path.name)
    (output / 'package-closure.json').write_text(json.dumps(closure, indent=2) + '\n')
    packaging_stable = all(digest(packaging_root / p) == value for p, value in packaging_before.items())
    if not packaging_stable: raise RuntimeError('Packaging source changed during this consumer verification.')
    receipt = dict(outside_source_tree=not scratch.is_relative_to(root), pristine_machine=False,
        isolated_package_cache=True, rust_trap_fired=False, native_unchanged_after_managed_change=True,
        native_sha256=digest(native), executable_sha256=digest(executable), jit_assembly_sha256=digest(dll),
        package_sha256={p.name: digest(p) for p in [core, facade]},
        package_bytes={p.name: p.stat().st_size for p in [core, facade]},
        build_tool_bytes=(task_dir / 'NativeWeave.Packaging.Tasks.dll').stat().st_size,
        build_record=build_record, publish_record=publish_record,
        source_snapshots={p.name: digest(p) for p in snapshots.iterdir()},
        reused_packaging_sources=packaging_before, packaging_source_commit=packaging_commit,
        packaging_source_dirty=packaging_dirty, packaging_source_stable=packaging_stable,
        scope='Actual codec/Core local NuGet; exact JIT/AOT wrapper failure and boundary cases, not pristine-machine or public-release acceptance.')
    (output / 'package-consumption.json').write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt
