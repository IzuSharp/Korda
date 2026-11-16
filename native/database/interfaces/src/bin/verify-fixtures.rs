//! Executes actual Rust contract validation against managed-produced golden bytes.
use nativeweave_data_contracts::wire::{Batch, Descriptor, Expectation, Relation, hex};
use nativeweave_data_contracts::{Codec, MetadataRegistry, Model, Profile, Projection};
use std::{fs, path::PathBuf, sync::Arc};

fn main() {
    let root = PathBuf::from(std::env::args().nth(1).expect("fixture directory"));
    let read = |name: &str| fs::read(root.join(name)).expect("fixture bytes");
    let parent_bytes = read("parent.json");
    let child_bytes = read("child.json");
    let parent =
        Arc::new(Model::parse(&parent_bytes, Profile::Sqlite).expect("managed parent metadata"));
    let child = Arc::new(
        Model::parse(&child_bytes, Profile::Sqlite).expect("new entity without Rust recompilation"),
    );
    assert_eq!(parent.encode(), parent_bytes);
    assert_eq!(child.encode(), child_bytes);
    assert_eq!(
        Model::parse(&read("unicode-model.json"), Profile::Sqlite)
            .expect("Unicode canonical rules")
            .encode(),
        read("unicode-model.json")
    );
    assert!(Model::parse(&read("configured-model.json"), Profile::Sqlite).is_err());
    Model::parse(&read("configured-model.json"), Profile::PostgreSql)
        .expect("configured decimal metadata");
    let projection_bytes = read("projection.json");
    let projection =
        Projection::parse(&projection_bytes, Profile::Sqlite).expect("exact ordered projection");
    assert_eq!(projection.encode(), projection_bytes);
    let relation_bytes = read("relation.bin");
    let relation = Relation::parse(&relation_bytes, Arc::clone(&parent), Arc::clone(&child))
        .expect("canonical kind23");
    assert_eq!(relation.bytes(), read("relation.json"));
    assert_eq!(relation.encode_record(), relation_bytes);
    let key_expected = relation
        .parent_key_expectation(Profile::Sqlite, &[3])
        .expect("key expectation");
    let key_bytes = read("key.bin");
    let key = Batch::parse(&key_bytes, key_expected.clone()).expect("parent-key db-wire");
    assert_eq!(
        key.cell(0, 0).expect("key cell"),
        Some(123i64.to_le_bytes().as_slice())
    );
    assert_eq!(
        Batch::encode(&key_expected, 1, &[Some(&123i64.to_le_bytes())])
            .expect("native key encoding"),
        key_bytes
    );
    let descriptors = (1..=19)
        .map(|i| {
            let (id, config, nullable) = match i {
                13 => (13, Some("{\"precision\":18,\"scale\":2}".into()), false),
                15 => (15, Some("{\"scale\":2}".into()), false),
                18 => (5, None, true),
                19 => (6, None, false),
                _ => (i, None, false),
            };
            Descriptor {
                id: i,
                codec: Codec {
                    id,
                    configuration: config,
                    nullable,
                },
                bound_codec: if i == 13 || i == 15 { 1023 + i } else { id },
            }
        })
        .collect::<Vec<_>>();
    let expected = Expectation {
        kind: 1,
        profile: Profile::PostgreSql,
        hash: [0; 32],
        entity_id: 0,
        projection_id: 0,
        descriptors,
    };
    let values_bytes = read("values.bin");
    let values =
        Batch::parse(&values_bytes, expected.clone()).expect("all scalar wire representations");
    // Independent native value construction, rather than reusing managed cell bytes as expected values.
    let decimal = |coefficient: u128, scale: u8, negative: bool| {
        let mut b = coefficient.to_le_bytes().to_vec();
        b[12] = scale;
        b[13] = u8::from(negative);
        b
    };
    let raw: Vec<Option<Vec<u8>>> = vec![
        Some((-7i16).to_le_bytes().to_vec()),
        Some(42i32.to_le_bytes().to_vec()),
        Some(i64::MAX.to_le_bytes().to_vec()),
        Some(vec![1]),
        Some("e\u{301} 🦀".as_bytes().to_vec()),
        Some(vec![0, 1, 255]),
        Some(vec![
            0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee,
            0xff,
        ]),
        Some(0i32.to_le_bytes().to_vec()),
        Some(123456u64.to_le_bytes().to_vec()),
        Some(0i64.to_le_bytes().to_vec()),
        Some((-0.0f64).to_le_bytes().to_vec()),
        Some(1.5f32.to_le_bytes().to_vec()),
        Some(decimal(1234, 2, false)),
        Some(decimal(123, 2, true)),
        Some(123456i64.to_le_bytes().to_vec()),
        Some(b"{\"v\":1}".to_vec()),
        Some(42i32.to_le_bytes().to_vec()),
        None,
        Some(vec![]),
    ];
    let refs = raw.iter().map(|v| v.as_deref()).collect::<Vec<_>>();
    assert_eq!(
        Batch::encode(&expected, 1, &refs).expect("independent native scalar encoding"),
        values_bytes
    );
    assert_eq!(values.cell(0, 17).expect("NULL cell"), None);
    assert_eq!(values.cell(0, 18).expect("empty cell"), Some([].as_slice()));

    let mut malformed = 0;
    // Canonical metadata rejects both semantic corruption and lossy JSON normalization.
    for (from, to) in [
        ("\"version\":1", "\"version\":2"),
        ("\"version\":1", "\"version\":1,\"version\":1"),
        ("\"relations\":[]", "\"relations\":[1]"),
        ("\"codec\":3", "\"codec\":99"),
        (
            "\"nullable\":false,\"key_ordinal\":0",
            "\"nullable\":true,\"key_ordinal\":0",
        ),
    ] {
        let mutated = String::from_utf8(parent_bytes.clone())
            .expect("UTF8")
            .replacen(from, to, 1);
        assert_ne!(mutated.as_bytes(), parent_bytes);
        assert!(Model::parse(mutated.as_bytes(), Profile::Sqlite).is_err());
        malformed += 1;
    }
    let mut space = parent_bytes.clone();
    space.push(b' ');
    assert!(Model::parse(&space, Profile::Sqlite).is_err());
    malformed += 1;
    let mut utf8 = parent_bytes.clone();
    utf8[20] = 255;
    assert!(Model::parse(&utf8, Profile::Sqlite).is_err());
    malformed += 1;
    for (offset, bytes) in [
        (4, vec![2]),
        (12, vec![1]),
        (76, vec![9]),
        (68, vec![1]),
        (56, u32::MAX.to_le_bytes().to_vec()),
        (88, u64::MAX.to_le_bytes().to_vec()),
        (108, vec![1]),
        (100, vec![99]),
    ] {
        let mut copy = values_bytes.clone();
        copy[offset..offset + bytes.len()].copy_from_slice(&bytes);
        assert!(Batch::parse(&copy, expected.clone()).is_err());
        malformed += 1;
    }
    let cells = 96 + expected.descriptors.len() * 16;
    for (offset, bytes) in [
        (cells + 8, 0u64.to_le_bytes().to_vec()),
        (cells + 16, u64::MAX.to_le_bytes().to_vec()),
        (cells + 2, vec![1]),
        (cells + 17 * 24, vec![6]),
    ] {
        let mut copy = values_bytes.clone();
        copy[offset..offset + bytes.len()].copy_from_slice(&bytes);
        assert!(Batch::parse(&copy, expected.clone()).is_err());
        malformed += 1;
    }
    let mut overlap = values_bytes.clone();
    let first_offset = overlap[cells + 8..cells + 16].to_vec();
    overlap[cells + 32..cells + 40].copy_from_slice(&first_offset);
    assert!(Batch::parse(&overlap, expected.clone()).is_err());
    malformed += 1;
    for index in [3, 4, 10, 12] {
        let mut copy = values_bytes.clone();
        let entry = cells + index * 24;
        let start =
            u64::from_le_bytes(copy[entry + 8..entry + 16].try_into().expect("width")) as usize;
        match index {
            3 => copy[start] = 2,
            4 => copy[start] = 255,
            10 => copy[start..start + 8].copy_from_slice(&f64::NAN.to_le_bytes()),
            12 => copy[start + 15] = 1,
            _ => unreachable!(),
        }
        assert!(Batch::parse(&copy, expected.clone()).is_err());
        malformed += 1;
    }
    // Every required relation field is checked, including swapped model identities and wrong hash.
    for offset in [0, 2, 12, 16, 18, 20, 24, relation_bytes.len() - 1] {
        let mut copy = relation_bytes.clone();
        copy[offset] ^= 1;
        assert!(Relation::parse(&copy, Arc::clone(&parent), Arc::clone(&child)).is_err());
        malformed += 1;
    }
    assert!(Relation::parse(&relation_bytes, Arc::clone(&child), Arc::clone(&parent)).is_err());
    malformed += 1;
    let mut registry =
        MetadataRegistry::new(Profile::Sqlite, 1, parent_bytes.len()).expect("registry");
    let first = registry
        .register(&parent_bytes, parent.hash())
        .expect("atomic registration");
    let second = registry
        .register(&parent_bytes, parent.hash())
        .expect("deduplication");
    assert!(Arc::ptr_eq(&first, &second));
    let before = registry.counts();
    assert!(registry.register(&child_bytes, child.hash()).is_err());
    assert_eq!(before, registry.counts());
    assert!(registry.register(&parent_bytes, [0; 32]).is_err());
    assert_eq!(before, registry.counts());
    assert!(Profile::try_from(0).is_err());
    assert!(Profile::try_from(3).is_err());
    println!(
        "{{\"status\":\"passed\",\"platform\":\"linux-x64\",\"cross_language_goldens\":7,\"malformed_rejections\":{malformed},\"scalar_kinds\":17,\"registry_atomicity\":true,\"parent_hash\":\"{}\",\"relation_hash\":\"{}\"}}",
        hex(&parent.hash()),
        hex(&relation.hash())
    );
}
