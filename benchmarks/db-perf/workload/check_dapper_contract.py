#!/usr/bin/env python3
"""Bind generated Dapper SQL constants to the exact shared read manifest."""
import json
from pathlib import Path
import re


def validate(manifest_path: Path, source_path: Path):
    manifest=json.loads(manifest_path.read_text())
    text=source_path.read_text()
    postgres=text.split('#if POSTGRES',1)[1].split('#else',1)[0]
    sqlite=text.split('#else',1)[1].split('#endif',1)[0]
    reads=[query for query in manifest['queries'] if 'lifecycle' not in query]
    for provider,block in [('postgresql',postgres),('sqlite',sqlite)]:
        actual={int(index):json.loads(value) for index,value in re.findall(r'private const string Sql(\d+)\s*=\s*("(?:[^"\\]|\\.)*");',block)}
        if len(actual)!=len(reads): raise ValueError('Generated read SQL inventory differs from manifest')
        for index,query in enumerate(reads):
            expected=query['sql'][provider]
            def name(ordinal,param):
                return '@'+(param['name'] if query['name']=='related' else 'pattern' if param['kind']=='string' else 'p'+str(ordinal))
            if provider=='postgresql':
                for ordinal,param in reversed(list(enumerate(query['parameters'],1))):
                    expected=re.sub(r'\$'+str(ordinal)+r'(?![0-9])',name(ordinal,param),expected)
            else:
                parameters=iter(enumerate(query['parameters'],1))
                expected=re.sub(r'\?',lambda _:name(*next(parameters)),expected)
            if actual[index]!=expected:
                raise ValueError(f'Dapper {provider} {query["name"]} SQL differs from frozen manifest')
    lifecycle=[query for query in manifest['queries'] if 'lifecycle' in query]
    if lifecycle:
        if {query['name'] for query in lifecycle}!={'tx-commit','tx-rollback','partial-update','stale-conflict','early-stop'}:
            raise ValueError('Unknown lifecycle contract inventory')
        source=(source_path.parent/'LifecycleCases.cs').read_text()
        constants={name:json.loads(value) for name,value in re.findall(r'private const string (\w+)\s*=\s*("(?:[^"\\]|\\.)*");',source)}
        for query in lifecycle:
            for name,value in query['lifecycle'].get('independentOrmSql',{}).items():
                if constants.get(name)!=value:raise ValueError(f'Dapper lifecycle {name} differs from its observed ORM SQL contract')
    return manifest


if __name__=='__main__':
    here=Path(__file__).resolve().parent
    result=validate(here/'queries-v3.json',here.parent/'managed/Dapper/Contracts.generated.cs')
    print('Verified',len(result['queries']),'Dapper read/lifecycle contracts for both providers')
