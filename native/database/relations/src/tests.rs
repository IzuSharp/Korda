use crate::*;
use nativeweave_data_contracts::{
    Projection, stable_id,
    wire::{Batch, Descriptor, Expectation},
};
use nativeweave_query_binding::{CodecBindings, ProjectionBinding, SourceIdentity};
use std::sync::Arc;

type Field = (u16, u16, Vec<u8>);
fn record(kind: u16, fields: &[Field]) -> Vec<u8> {
    let size = 16 + 16 * fields.len() + fields.iter().map(|f| f.2.len()).sum::<usize>();
    let mut bytes = vec![0; size];
    bytes[..2].copy_from_slice(&1u16.to_le_bytes());
    bytes[2..4].copy_from_slice(&kind.to_le_bytes());
    bytes[4..8].copy_from_slice(&(size as u32).to_le_bytes());
    bytes[8..12].copy_from_slice(&(fields.len() as u32).to_le_bytes());
    let mut at = 16 + 16 * fields.len();
    for (i, (id, ty, value)) in fields.iter().enumerate() {
        let o = 16 + i * 16;
        bytes[o..o + 2].copy_from_slice(&id.to_le_bytes());
        bytes[o + 2..o + 4].copy_from_slice(&ty.to_le_bytes());
        bytes[o + 4..o + 8].copy_from_slice(&(value.len() as u32).to_le_bytes());
        bytes[o + 8..o + 16].copy_from_slice(&(at as u64).to_le_bytes());
        bytes[at..at + value.len()].copy_from_slice(value);
        at += value.len();
    }
    bytes
}
fn model(profile: Profile) -> ModelBinding {
    let logical = "NativeWeave.Orm.Relations.Tests:Fixture";
    let name = "b14_items";
    let table_id = stable_id(&[Some("NWDB-TABLE-ID-v1"), Some(logical), None, Some(name)]);
    let names = ["Tenant", "Id", "Name", "Version"];
    let ids: Vec<_> = names
        .iter()
        .map(|n| {
            stable_id(&[
                Some("NWDB-COLUMN-ID-v1"),
                Some(logical),
                None,
                Some(name),
                Some(n),
            ])
        })
        .collect();
    let mut columns:Vec<_>=names.iter().enumerate().map(|(i,n)| (ids[i],format!("{{\"id\":{},\"name\":\"{n}\",\"codec\":{},\"codec_config\":null,\"nullable\":{},\"key_ordinal\":{},\"generated\":false,\"concurrency\":{},\"collation\":null,\"max_length\":null}}",ids[i],if i==2 {5}else{3},i==2,if i<2{i.to_string()}else{"null".into()},i==3))).collect();
    columns.sort_by_key(|p| p.0);
    let text = format!(
        "{{\"version\":1,\"logical_id\":\"{logical}\",\"table_id\":{table_id},\"schema\":null,\"table\":\"{name}\",\"columns\":[{}],\"projection\":[{}],\"relations\":[]}}",
        columns
            .iter()
            .map(|p| p.1.as_str())
            .collect::<Vec<_>>()
            .join(","),
        ids.iter().map(u32::to_string).collect::<Vec<_>>().join(",")
    );
    let source = SourceIdentity {
        engine: 1,
        source: 1,
        generation: 1,
        profile,
    };
    ModelBinding::new(
        source,
        Arc::new(Model::parse(text.as_bytes(), profile).unwrap()),
        Arc::new(CodecBindings::new(profile, &[]).unwrap()),
    )
    .unwrap()
}
fn values(model: &ModelBinding, names: &[&str], cells: &[Option<Vec<u8>>]) -> Vec<u8> {
    let descriptors = names
        .iter()
        .map(|name| {
            let c = model
                .model
                .columns()
                .iter()
                .find(|c| &c.name == name)
                .unwrap();
            Descriptor {
                id: c.id,
                codec: c.codec.clone(),
                bound_codec: model.codecs.bound(&c.codec).unwrap(),
            }
        })
        .collect();
    Batch::encode(
        &Expectation {
            kind: 4,
            profile: model.source.profile,
            hash: model.model.hash(),
            entity_id: model.model.table_id(),
            projection_id: 0,
            descriptors,
            exact_rows: Some(1),
        },
        1,
        &cells.iter().map(|v| v.as_deref()).collect::<Vec<_>>(),
    )
    .unwrap()
}
fn integer(value: i64) -> Option<Vec<u8>> {
    Some(value.to_le_bytes().to_vec())
}
fn fields(model: &ModelBinding, kind: u32) -> Vec<Field> {
    let insert = values(
        model,
        &["Tenant", "Id", "Name", "Version"],
        &[integer(7), integer(1), Some(b"insert".to_vec()), integer(4)],
    );
    let update = values(model, &["Name", "Version"], &[None, integer(9)]);
    let key = values(model, &["Tenant", "Id"], &[integer(7), integer(1)]);
    let checks = values(model, &["Version"], &[integer(4)]);
    let mut f = vec![(1, 1, model.model.table_id().to_le_bytes().to_vec())];
    match kind {
        0 => f.push((2, 5, insert)),
        1 => f.extend([(2, 5, insert), (7, 1, 1u32.to_le_bytes().to_vec())]),
        2 => f.extend([(2, 5, update), (3, 5, key)]),
        3 => f.extend([(2, 5, update), (3, 5, key), (4, 5, checks)]),
        4 => f.push((3, 5, key)),
        5 => f.extend([(3, 5, key), (4, 5, checks)]),
        6 => f.extend([
            (2, 5, insert),
            (
                5,
                7,
                model
                    .model
                    .keys()
                    .iter()
                    .flat_map(|c| c.id.to_le_bytes())
                    .collect(),
            ),
            (8, 5, update),
        ]),
        _ => unreachable!(),
    };
    f
}
fn projection(model: &ModelBinding) -> ProjectionBinding {
    ProjectionBinding::new(model.source,1,Arc::new(Projection::parse(br#"{"version":1,"fields":[{"ordinal":0,"name":"Id","codec":3,"codec_config":null,"nullable":false}]}"#,model.source.profile).unwrap()),model.codecs.clone()).unwrap()
}
#[test]
fn exact_mutation_inventory_and_independent_upsert_values() {
    for profile in [Profile::Sqlite, Profile::PostgreSql] {
        let model = model(profile);
        for kind in 0..=6 {
            let fields = fields(&model, kind);
            let projection = (kind == 1).then(|| projection(&model));
            let result = compile_mutation(
                &model,
                kind,
                &record(4, &fields),
                projection.clone(),
                Limits::default(),
                1_000_000_000,
            )
            .unwrap();
            assert_eq!(result.checked, kind == 3 || kind == 5);
            assert_eq!(result.key_bound, matches!(kind, 2..=5));
            for removed in 0..fields.len() {
                let mut invalid = fields.clone();
                invalid.remove(removed);
                assert!(
                    compile_mutation(
                        &model,
                        kind,
                        &record(4, &invalid),
                        projection.clone(),
                        Limits::default(),
                        1
                    )
                    .is_err()
                );
            }
            for extra in 1..=8 {
                if !fields.iter().any(|f| f.0 == extra) {
                    let mut invalid = fields.clone();
                    invalid.push((extra, 1, 1u32.to_le_bytes().to_vec()));
                    invalid.sort_by_key(|f| f.0);
                    assert!(
                        compile_mutation(
                            &model,
                            kind,
                            &record(4, &invalid),
                            projection.clone(),
                            Limits::default(),
                            1
                        )
                        .is_err()
                    );
                }
            }
            if kind == 6 {
                let command = result.command;
                assert!(command.sql.contains("DO UPDATE SET"));
                assert!(!command.sql.contains("excluded"));
                let batch =
                    Batch::parse(&command.parameter_bytes, command.parameter_expectation).unwrap();
                assert_eq!(batch.cell(0, 2).unwrap(), Some(b"insert".as_slice()));
                assert_eq!(batch.cell(0, 4).unwrap(), None);
                assert_eq!(
                    batch.cell(0, 5).unwrap(),
                    Some(9i64.to_le_bytes().as_slice())
                );
            }
        }
    }
}
#[test]
fn composite_key_tenant_and_caller_version_survive_native_mutation() {
    for profile in [Profile::Sqlite, Profile::PostgreSql] {
        let model = model(profile);
        let fields = fields(&model, 3);
        let rendered = compile_mutation(&model, 3, &record(4, &fields), None, Limits::default(), 1)
            .unwrap()
            .command;
        assert!(rendered.sql.contains("\"Tenant\" ="));
        assert!(rendered.sql.contains("\"Id\" ="));
        assert!(rendered.sql.contains("\"Version\" ="));
        assert!(!rendered.sql.contains("+ 1"));
        let batch =
            Batch::parse(&rendered.parameter_bytes, rendered.parameter_expectation).unwrap();
        assert_eq!(
            batch.cell(0, 1).unwrap(),
            Some(9i64.to_le_bytes().as_slice())
        );
        assert_eq!(
            batch.cell(0, 2).unwrap(),
            Some(7i64.to_le_bytes().as_slice())
        );
        assert_eq!(
            batch.cell(0, 4).unwrap(),
            Some(4i64.to_le_bytes().as_slice())
        );
        let mut reordered = fields.clone();
        reordered[2].2 = values(&model, &["Id", "Tenant"], &[integer(1), integer(7)]);
        assert!(
            compile_mutation(
                &model,
                3,
                &record(4, &reordered),
                None,
                Limits::default(),
                1
            )
            .is_err()
        );
        assert!(
            compile_mutation(
                &model,
                3,
                &record(4, &fields),
                None,
                Limits {
                    parameters: 2,
                    ..Limits::default()
                },
                1
            )
            .is_err()
        );
        let mut generated = fields.clone();
        generated[1].2 = values(&model, &["Id"], &[integer(1)]);
        assert!(
            compile_mutation(
                &model,
                3,
                &record(4, &generated),
                None,
                Limits::default(),
                1
            )
            .is_err()
        );
    }
}
