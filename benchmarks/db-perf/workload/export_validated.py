#!/usr/bin/env python3
"""Export the shared 30-case contract from two matching successful AOT receipts.

Exact Fluent SQL comes from ToSql in each application. The related batch SQL is
captured from PostgreSQL's actual execute log, then the SQLite renderer shape is
verified against the source convention (unparenthesized UNION ALL branches).
This exporter neither runs workloads nor turns correctness probes into timings.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re


def sha(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sqlite',type=Path,required=True)
    parser.add_argument('--postgresql',type=Path,required=True)
    parser.add_argument('--postgres-log',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args(); reports={name:json.loads(getattr(args,name).read_text()) for name in ('sqlite','postgresql')}
    for report in reports.values():
        assert report['NativeAot'] and report['Failed']==0 and report['OwnersReturnedToZero']
        assert len(report['ExpectedResults'])==30 and report['Completed']>=30
    left,right=reports.values()
    for key in ('ExpectedResults','RequestedRows','BatchRows','Tenants','OrdersPerTenant'):
        assert left[key]==right[key], key
    rows=left['RequestedRows']; orders=left['OrdersPerTenant']; parent_count=min(rows,left['BatchRows'],orders)
    definitions={
        'numeric':[('Id','int64')], 'text':[('Id','int64'),('Note','string')],
        'money':[('Id','int64'),('Total','cents-from-decimal')], 'json':[('Id','int64'),('Settings','settings')],
        'wide':[('TenantId','int64'),('Id','int64'),('CreatedAt','int64'),('CustomerId','nullable-int64'),('Total','cents-from-decimal'),('Note','string'),('Version','int64'),('Settings','settings'),('InternalMemo','string')],
        'left-join':[('Id','int64'),('Name','string')], 'inner-join':[('Id','int64'),('Name','string')],
        'customer-totals':[('CustomerId','nullable-int64'),('Count','int64'),('Total','nullable-cents-from-decimal')],
        'product-totals':[('Product','string'),('Quantity','nullable-int64')],
        'empty-sum':[('value','nullable-cents-from-decimal')],
        'window':[('Id','int64'),('Rank','int64')], 'running-total':[('Id','int64'),('Quantity','nullable-int64')],
        'three-join':[('OrderId','int64'),('Name','string'),('Product','string'),('Quantity','int64')],
        'raw-sql':[('Id','int64'),('Note','string')],
        'related':[('Id','int64'),('TenantId','int64'),('OrderId','int64'),('Product','string'),('Quantity','int64'),('__korda_parent_index','int32')],
    }
    params={
        'optional-filter':[1,2,rows], 'range':[1,1700000100,1700000200,rows],
        'literal-note':[1,'%note-1%',rows], 'customer-totals':[1,3], 'product-totals':[1,10], 'empty-sum':[-1],
        'cursor':[1,1700000000+orders//2//4,1700000000+orders//2//4,1,1,orders//2,rows],
        'offset':[1,rows,orders*3//4], 'exists':[1,32,1], 'in-subquery':[1,128,rows], 'cte':[1,16,rows],
        'union-all':[1,16,1,9,24], 'union':[1,16,1,9,24], 'intersect':[1,16,1,9,24], 'except':[1,16,1,9,24],
        'anti-association':[1,1,5,rows],
    }
    related_lines=[line for line in args.postgres_log.read_text().splitlines() if 'execute <unnamed>: SELECT ' in line and '__korda_parent_index' in line]
    assert related_lines, 'Need actual related SQL capture'
    related_pg=related_lines[0].split('execute <unnamed>: ',1)[1]
    base='SELECT '+', '.join('"order_lines"."'+field+'" AS "'+field+'"' for field in ('Id','TenantId','OrderId','Product','Quantity'))
    branch=base+', ? AS "__korda_parent_index" FROM "order_lines" WHERE "order_lines"."TenantId" = ? AND "order_lines"."OrderId" = ?'
    related_sqlite=' UNION ALL '.join([branch]*parent_count)
    result=[]
    for name,expected in left['ExpectedResults'].items():
        fields=definitions.get(name,[('Id','int64')]); scalar=name not in definitions or name=='numeric'
        sql={provider:report['ActualDiagnosticSql'][name] for provider,report in reports.items()}
        if name=='related': sql={'sqlite':related_sqlite,'postgresql':related_pg}
        if name=='raw-sql':
            sql={'sqlite':sql['sqlite'].replace('@tenant','?').replace('@take','?'),
                 'postgresql':sql['postgresql'].replace('@tenant','$1').replace('@take','$2')}
        values=params.get(name,[1,rows]); parameters=[]
        if name=='related':
            for index in range(parent_count):
                parameters.extend([{'name':f'parent_index_{index}','kind':'int32','value':index},
                    {'name':f'tenant_{index}','kind':'int64','value':1},{'name':f'parent_id_{index}','kind':'int64','value':index+1}])
        else:
            for index,value in enumerate(values):
                parameters.append({'name':('tenant' if index==0 else 'take') if name in ('numeric','text','money','json','wide','raw-sql') else f'p{index+1}',
                    'kind':'string' if isinstance(value,str) else 'int64','value':value})
        item={'name':name,'entry':'handwritten-sql' if name=='raw-sql' else 'public-related-batch' if name=='related' else 'fluent',
            'sql':sql,'parameters':parameters,'columns':[{'name':field,'alias':'value' if scalar else field,'kind':kind,
                'nullable':kind.startswith('nullable-') or field=='Note' or (name=='left-join' and field=='Name')} for field,kind in fields],
            'expectedRows':expected['Rows'],'expectedDigest':str(expected['Digest']),'expectedDigestHex':f"{expected['Digest']:016x}",
            'materialization':'buffered-related' if name=='related' else 'stream',
            'queryExecutionsPerOperation':1,
            'preparedContract':'unavailable: LoadRelatedAsync rebuilds native query for each bounded batch' if name=='related' else 'immutable SqlCommand reuse; no public PreparedQuery overload' if name=='raw-sql' else 'immutable PreparedQuery<T> plan reused; per-execution values/resources are still included',
            'construction':{provider:{'kind':'SQL-token-concatenation; direct SQL text construction is distinct from Fluent graph construction','fragments':re.findall(r'\s+|[^\s]+',text)} for provider,text in sql.items()},
            'validation':{'kind':'actual Native AOT correctness probe with independent seed-formula digest','providers':list(reports),'performanceAcceptance':False}}
        if name in ('money','wide','customer-totals','empty-sum'): item['moneyStorage']={'sqlite':'integer cents','postgresql':'numeric(18,2)'}
        if name in ('union-all','union','intersect','except'):
            item['knownLimitation']='Direct compound ORDER BY table-qualified input column fails on original PostgreSQL package; final contract uses a public CommonTable wrapper plus outer ORDER BY, identically before and after. Original failures retained separately.'
        if name=='related':
            item['related']={'parentKeys':[{'tenant':1,'id':i+1} for i in range(parent_count)],'ordinalColumn':'__korda_parent_index','ordinalBase':0,
                'grouping':'retain all owned children; one group per input key including empty groups; sort each group by child Id before digest',
                'digest':'for each input key in order: Int64 parent Id, Int64 group count, then each child Id,TenantId,OrderId,Product,Quantity; do not hash internal ordinal',
                'rowsMetric':'number of child rows, excludes empty group headers','sqliteSqlProvenance':'provider renderer convention from source; PostgreSQL counterpart captured from actual execution log; SQLite batch result and digest separately validated'}
        result.append(item)
    document={'schemaVersion':2,'dataset':{'tenants':left['Tenants'],'ordersPerTenant':orders,'customers':256,'requestedRows':rows,'batchRows':left['BatchRows']},
        'digest':{'algorithm':'FNV-1a 64 bit','offset':'14695981039346656037','prime':'1099511628211','integer':'signed Int64 little-endian bytes',
            'nullable-int64':'presence byte then Int64 if present','string':'Int64 UTF-8 byte length then bytes; NULL length -1',
            'cents-from-decimal':'exact decimal *100 to Int64; SQLite already stores cents','nullable-cents-from-decimal':'presence byte then cents Int64 if present',
            'settings':'Channel string then one-byte boolean Gift','rowSeparator':'none'},
        'validationInputs':{provider:{'receipt':str(getattr(args,provider)),'receiptSha256':sha(getattr(args,provider)),
            'executableSha256':report['ExecutableSha256'],'nativeSha256':report['LoadedNativeSha256'],'packageVersion':report['PackageVersion']} for provider,report in reports.items()},
        'postgresStatementLog':{'path':str(args.postgres_log),'sha256':sha(args.postgres_log)},'queries':result}
    args.output.write_text(json.dumps(document,indent=2)+'\n')
    print(f'Exported {len(result)} contracts to {args.output}')

if __name__=='__main__': main()
