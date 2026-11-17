"""Independent local compiler-package consumer; no production package/release claim."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import zipfile


def consume(root, output, native, run, base_env):
    scratch = Path(tempfile.mkdtemp(prefix='nativeweave-a10-consumer-'))
    packages = output / 'packages'
    package = packages / 'NativeWeave.Generators.CSharp.Metadata.0.1.0-preview.1.nupkg'
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        assert names.count('tools/roslyn/NativeWeave.Generators.CSharp.Metadata.dll') == 1
        assert not any(name.startswith(('analyzers/', 'lib/')) for name in names)
        assert names.count('buildTransitive/NativeWeave.Generators.CSharp.Metadata.targets') == 1
    env = base_env.copy()
    env['NUGET_PACKAGES'] = str(scratch / 'isolated-packages')
    trap = scratch / 'rust-trap'
    trap.mkdir()
    marker = scratch / 'unexpected-rust.txt'
    for tool in ['cargo', 'rustc', 'rustup']:
        path = trap / tool
        path.write_text('#!/bin/sh\nprintf "unexpected Rust invocation\\n" >> "' + str(marker) + '"\nexit 99\n')
        path.chmod(0o755)
    env['PATH'] = str(trap) + os.pathsep + env['PATH']
    config = scratch / 'NuGet.Config'
    config.write_text('<configuration><packageSources><clear/><add key="fixture" value="' + str(packages) + '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    shutil.copy2(root / 'global.json', scratch / 'global.json')
    facade = scratch / 'facade'
    facade.mkdir()
    (facade / 'Facade.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><PackageId>NativeWeave.Conformance.MetadataFacade</PackageId><Version>1.0.0</Version><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetAudit>true</NuGetAudit></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Generators.CSharp.Metadata" Version="0.1.0-preview.1" /></ItemGroup></Project>''')
    (facade / 'Marker.cs').write_text('namespace Conformance; public sealed class Marker { }\n')
    run('package-facade-pack', ['dotnet', 'pack', facade / 'Facade.csproj', '-c', 'Release', '-o', packages,
        '--configfile', config, '-p:UseSharedCompilation=false', '-nr:false', '-m:1'], cwd=scratch, environment=env)
    with zipfile.ZipFile(packages / 'NativeWeave.Conformance.MetadataFacade.1.0.0.nupkg') as archive:
        nuspec = archive.read('NativeWeave.Conformance.MetadataFacade.nuspec').decode()
        assert 'NativeWeave.Generators.CSharp.Metadata' in nuspec
    consumer = scratch / 'consumer'
    consumer.mkdir()
    source_metadata = root / 'tests/Generators.CSharp/Consumer/generated/Bindings.nwmetadata'
    shutil.copy2(source_metadata, consumer / 'Bindings.nwmetadata')
    fields = dict(line.split('\t', 1) for line in source_metadata.read_text().splitlines() if line.startswith(('module\t', 'digest\t', 'namespace\t')))
    identity = 'BindingMetadata_M_' + hashlib.sha256(fields['module'].encode()).hexdigest()
    project = consumer / 'Consumer.csproj'
    project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15.0</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile><NuGetLockFilePath Condition="'$(RuntimeIdentifier)' == 'linux-x64' or '$(RuntimeIdentifiers)' == 'linux-x64'">packages.linux-x64.lock.json</NuGetLockFilePath><NuGetAudit>true</NuGetAudit><AssemblyName>IndependentMetadataConsumer</AssemblyName></PropertyGroup><ItemGroup><PackageReference Include="NativeWeave.Conformance.MetadataFacade" Version="1.0.0"/><AdditionalFiles Include="Bindings.nwmetadata"/><Compile Remove="ManualMetadata.cs"/><Compile Include="ManualMetadata.cs" Condition="'$(NativeWeaveExcludeGenerators)' == 'true'"/></ItemGroup></Project>''')
    (consumer / 'Program.cs').write_text('using System.Runtime.CompilerServices;\nusing ' + fields['namespace'] + ';\n' +
        'static int Count<T>() => ' + identity + '.ExportCount;\n' +
        'if (Count<int>() != 10) throw new Exception("Metadata mismatch");\n' +
        'if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("AOT required");\n' +
        'Console.WriteLine("PASS independent transitive compiler package; closed generic metadata; dynamicCode=" + RuntimeFeature.IsDynamicCodeSupported);\n')
    (consumer / 'ManualMetadata.cs').write_text('namespace ' + fields['namespace'] + ';\ninternal static class ' + identity + ' { internal const int ExportCount = 10; internal const string SchemaSha256 = "' + fields['digest'] + '"; }\n')
    normal = ['-p:UseSharedCompilation=false', '-nr:false', '-m:1']
    before = hashlib.sha256(native.read_bytes()).hexdigest()
    run('package-consumer-restore', ['dotnet', 'restore', project, '--configfile', config, *normal], cwd=scratch, environment=env)
    run('package-consumer-build', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *normal], cwd=scratch, environment=env)
    items = json.loads(run('package-analyzer-items', ['dotnet', 'msbuild', project, '-getItem:Analyzer', *normal], cwd=scratch, environment=env))
    matching = [x for x in items['Items']['Analyzer'] if Path(x['Identity']).name == 'NativeWeave.Generators.CSharp.Metadata.dll']
    assert len(matching) == 1, matching
    assert not (consumer / 'bin/Release/net11.0/NativeWeave.Generators.CSharp.Metadata.dll').exists()
    run('package-consumer-jit', ['dotnet', consumer / 'bin/Release/net11.0/IndependentMetadataConsumer.dll'], cwd=scratch, environment=env)
    (consumer / 'AddedEntity.cs').write_text('internal sealed class AddedEntity { public int Value { get; set; } }\n')
    run('package-managed-addition', ['dotnet', 'build', project, '-c', 'Release', '--no-restore', *normal], cwd=scratch, environment=env)
    run('package-aot-publish', ['dotnet', 'publish', project, '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-o', output / 'package-aot', '--configfile', config, *normal], cwd=scratch, environment=env)
    run('package-aot-execute', [output / 'package-aot/IndependentMetadataConsumer', '--require-aot'], cwd=scratch, environment=env)
    # The explicit manual path uses the same static descriptor and no compiler fallback.
    run('package-manual-build', ['dotnet', 'build', project, '-c', 'Release', '-p:NativeWeaveExcludeGenerators=true', '--configfile', config, *normal], cwd=scratch, environment=env)
    run('package-manual-jit', ['dotnet', consumer / 'bin/Release/net11.0/IndependentMetadataConsumer.dll'], cwd=scratch, environment=env)
    assert not marker.exists(), 'Compiler package invoked Rust.'
    assert before == hashlib.sha256(native.read_bytes()).hexdigest(), 'Managed consumption changed the precompiled native component.'
    locks = {name: hashlib.sha256((consumer / name).read_bytes()).hexdigest()
        for name in ['packages.lock.json', 'packages.linux-x64.lock.json']}
    receipt = dict(consumer_directory=str(consumer), outside_source_tree=not consumer.is_relative_to(root),
        isolated_cache=str(env['NUGET_PACKAGES']), pristine_machine=False, rust_invocation_trap_fired=False,
        generator_analyzer_count=len(matching), generator_in_runtime_output=False, facade_dependency_retained=True,
        managed_entity_addition=True, native_sha256_before=before, native_sha256_after=before,
        scope='Independent compiler package, local conformance facade, JIT/AOT and handwritten metadata; not a production engine release.',
        packages={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in packages.glob('*.nupkg')},
        consumer_locks=locks)
    (output / 'package-consumption.json').write_text(json.dumps(receipt, indent=2) + '\n')
    source_copy = output / 'package-consumer-sources'
    source_copy.mkdir()
    for path in consumer.glob('*'):
        if path.is_file() and path.suffix in {'.cs', '.csproj', '.nwmetadata', '.json'}:
            shutil.copy2(path, source_copy / path.name)
    return receipt
