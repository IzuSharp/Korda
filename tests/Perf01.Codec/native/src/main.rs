//! Test-only strict direct miniz_oxide comparator. No NativeWeave/FFI/runtime.
//! The bounded core loop preserves full-input consumption and fallible output
//! reservations, unlike the upstream convenience decoder's trailing-data rule.
use miniz_oxide::deflate::core::{
    CompressorOxide, TDEFLFlush, TDEFLStatus, compress, create_comp_flags_from_zip_params,
};
use miniz_oxide::inflate::{
    TINFLStatus,
    core::{DecompressorOxide, decompress, inflate_flags},
};
use std::{
    env, fs,
    io::{self, BufRead, Write},
    path::Path,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
        mpsc,
    },
    thread,
    time::Instant,
};

const RAW: usize = 4 * 1024 * 1024;
const ENCODED: usize = RAW + 65536;
type Result<T> = std::result::Result<T, String>;
static OUTPUT: Mutex<()> = Mutex::new(());
static ERRORS: AtomicU64 = AtomicU64::new(0);

fn emit(text: &str) {
    let _gate = OUTPUT.lock().unwrap();
    let mut out = io::stdout().lock();
    writeln!(out, "{text}").unwrap();
    out.flush().unwrap();
}

fn buffer(size: usize) -> Result<Vec<u8>> {
    let mut output = Vec::new();
    output
        .try_reserve_exact(size)
        .map_err(|_| "reservation failure".to_owned())?;
    output.resize(size, 0);
    Ok(output)
}

fn grow(output: &mut Vec<u8>, cap: usize) -> Result<()> {
    let next = output.len().saturating_mul(2).max(8).min(cap);
    if next <= output.len() {
        return Err("output cap".to_owned());
    }
    output
        .try_reserve_exact(next - output.len())
        .map_err(|_| "reservation failure".to_owned())?;
    output.resize(next, 0);
    Ok(())
}

fn encode(input: &[u8]) -> Result<Vec<u8>> {
    if input.len() > RAW {
        return Err("raw cap".to_owned());
    }
    let mut output = buffer((input.len() / 2).max(8).min(ENCODED))?;
    let mut state = CompressorOxide::new(create_comp_flags_from_zip_params(6, 15, 0));
    let (mut read, mut written) = (0, 0);
    loop {
        let (status, used, produced) = compress(
            &mut state,
            &input[read..],
            &mut output[written..],
            TDEFLFlush::Finish,
        );
        if used > input.len() - read || produced > output.len() - written {
            return Err("extent".to_owned());
        }
        read += used;
        written += produced;
        match status {
            TDEFLStatus::Done if read == input.len() => {
                output.truncate(written);
                return Ok(output);
            }
            TDEFLStatus::Okay => {
                if output.len() - written < 30 || (used == 0 && produced == 0) {
                    grow(&mut output, ENCODED)?;
                }
            }
            _ => return Err("compress failure".to_owned()),
        }
    }
}

fn decode(input: &[u8], cap: usize) -> Result<Vec<u8>> {
    if input.len() > ENCODED || cap > RAW {
        return Err("input/profile cap".to_owned());
    }
    let mut output = buffer(input.len().saturating_mul(2).min(cap))?;
    let mut state = DecompressorOxide::new();
    let flags = inflate_flags::TINFL_FLAG_PARSE_ZLIB_HEADER
        | inflate_flags::TINFL_FLAG_USING_NON_WRAPPING_OUTPUT_BUF;
    let (mut read, mut written) = (0, 0);
    loop {
        let (status, used, produced) =
            decompress(&mut state, &input[read..], &mut output, written, flags);
        if used > input.len() - read || produced > output.len() - written {
            return Err("extent".to_owned());
        }
        read += used;
        written += produced;
        match status {
            TINFLStatus::Done if read == input.len() => {
                output.truncate(written);
                return Ok(output);
            }
            TINFLStatus::HasMoreOutput if output.len() < cap => grow(&mut output, cap)?,
            _ => return Err("invalid stream or output cap".to_owned()),
        }
    }
}

struct Corpus {
    raw: Vec<u8>,
    encoded: Vec<u8>,
}

fn raw(size: usize, compressible: bool) -> Vec<u8> {
    let mut seed = 0x9e3779b9u32;
    let mut pattern = [0u8; 64];
    for byte in &mut pattern {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        *byte = seed as u8;
    }
    seed = 0x9e3779b9;
    (0..size)
        .map(|i| {
            if compressible {
                pattern[i % 64]
            } else {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                seed as u8
            }
        })
        .collect()
}

fn make_corpus(path: &Path) -> Result<()> {
    fs::create_dir(path).map_err(|e| e.to_string())?;
    for (i, size) in [256, 4096, 1048576, 4194304].into_iter().enumerate() {
        for entropy in 0..2 {
            let input = raw(size, entropy == 0);
            let output = encode(&input)?;
            if output != miniz_oxide::deflate::compress_to_vec_zlib(&input, 6)
                || decode(&output, RAW)? != input
            {
                return Err("upstream parity / roundtrip".to_owned());
            }
            let index = i * 2 + entropy;
            fs::write(path.join(format!("{index}.raw")), input).map_err(|e| e.to_string())?;
            fs::write(path.join(format!("{index}.zlib")), output).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

fn preflight(corpus: &[Corpus]) -> Result<()> {
    for case in corpus {
        if encode(&case.raw)? != case.encoded || decode(&case.encoded, RAW)? != case.raw {
            return Err("corpus mismatch".to_owned());
        }
    }
    let valid = encode(b"limit")?;
    if decode(&valid, 1).is_ok()
        || encode(&vec![0; RAW + 1]).is_ok()
        || decode(&vec![0; ENCODED + 1], RAW).is_ok()
    {
        return Err("bounds negative failed".to_owned());
    }
    for i in 0..valid.len() {
        if decode(&valid[..i], RAW).is_ok() {
            return Err("truncation negative failed".to_owned());
        }
    }
    let mut corrupt = valid.clone();
    *corrupt.last_mut().unwrap() ^= 1;
    let mut trailing = valid.clone();
    trailing.push(0);
    let mut concat = valid.clone();
    concat.extend_from_slice(&valid);
    if [&corrupt, &trailing, &concat]
        .into_iter()
        .any(|b| decode(b, RAW).is_ok())
    {
        return Err("corruption/trailing negative failed".to_owned());
    }
    Ok(())
}

struct Work {
    id: u64,
    case: usize,
    encode: bool,
}

fn execute(work: Work, corpus: &[Corpus]) {
    emit(&format!("{{\"kind\":\"admitted\",\"id\":{}}}", work.id));
    let item = &corpus[work.case];
    let start = Instant::now();
    let value = if work.encode {
        encode(&item.raw)
    } else {
        decode(&item.encoded, RAW)
    };
    let call = start.elapsed().as_nanos();
    let (mut status, mut length) = ("ok", 0);
    if let Ok(ref bytes) = value {
        length = bytes.len();
        if bytes
            != if work.encode {
                &item.encoded
            } else {
                &item.raw
            }
        {
            status = "failed";
        }
    } else {
        status = "failed";
    }
    let consumed = start.elapsed().as_nanos();
    drop(value);
    let end = start.elapsed().as_nanos();
    if status != "ok" {
        ERRORS.fetch_add(1, Ordering::Relaxed);
    }
    emit(&format!(
        "{{\"kind\":\"completed\",\"id\":{},\"status\":\"{}\",\"raw_bytes\":{},\"encoded_bytes\":{},\"call_ns\":{},\"consume_ns\":{},\"release_ns\":{},\"operation_ns\":{},\"managed_allocated_bytes\":null,\"copied_bytes_derived\":0,\"ffi_calls_derived\":0}}",
        work.id,
        status,
        item.raw.len(),
        if work.encode {
            length
        } else {
            item.encoded.len()
        },
        call,
        consumed - call,
        end - consumed,
        end
    ));
}

fn run(args: &[String]) -> Result<()> {
    let value = |key: &str| -> Result<&str> {
        args.iter()
            .position(|x| x == key)
            .and_then(|i| args.get(i + 1))
            .map(String::as_str)
            .ok_or_else(|| format!("missing {key}"))
    };
    let path = Path::new(value("--corpus")?);
    if args.iter().any(|x| x == "--make-corpus") {
        return make_corpus(path);
    }
    let workers: usize = value("--workers")?
        .parse()
        .map_err(|_| "workers".to_owned())?;
    let capacity: usize = value("--queue")?.parse().map_err(|_| "queue".to_owned())?;
    if !(1..=256).contains(&workers) || capacity == 0 {
        return Err("invalid worker/queue configuration".to_owned());
    }
    let corpus: Vec<Corpus> = (0..8)
        .map(|i| {
            Ok(Corpus {
                raw: fs::read(path.join(format!("{i}.raw"))).map_err(|e| e.to_string())?,
                encoded: fs::read(path.join(format!("{i}.zlib"))).map_err(|e| e.to_string())?,
            })
        })
        .collect::<Result<_>>()?;
    preflight(&corpus)?;
    if args.iter().any(|x| x == "--check-only") {
        emit("{\"kind\":\"functional_check_passed\"}");
        return Ok(());
    }
    let corpus = Arc::new(corpus);
    let (sender, receiver) = mpsc::sync_channel::<Work>(capacity);
    let receiver = Arc::new(Mutex::new(receiver));
    let mut handles = Vec::new();
    for _ in 0..workers {
        let receiver = Arc::clone(&receiver);
        let corpus = Arc::clone(&corpus);
        handles.push(thread::spawn(move || {
            loop {
                let work = receiver.lock().unwrap().recv();
                match work {
                    Ok(work) => execute(work, &corpus),
                    Err(_) => break,
                }
            }
        }));
    }
    emit(&format!(
        "{{\"kind\":\"ready\",\"dynamic_code_supported\":false,\"workers\":{workers},\"queue_capacity\":{capacity},\"strict_single_stream_comparable\":true}}"
    ));
    let stdin = io::stdin();
    let mut lines = stdin.lock().lines();
    for line in lines.by_ref() {
        let line = line.map_err(|e| e.to_string())?;
        if line == "drain" {
            break;
        }
        if line == "snapshot" {
            emit("{\"kind\":\"snapshot\",\"native\":null}");
            continue;
        }
        let fields: Vec<_> = line.split('\t').collect();
        if fields.len() != 4 || !matches!(fields[2], "c" | "d") || !matches!(fields[3], "a" | "o") {
            return Err("invalid frame".to_owned());
        }
        let id = fields[0].parse::<u64>().map_err(|_| "id".to_owned())?;
        let case = fields[1].parse::<usize>().map_err(|_| "case".to_owned())?;
        if case >= 8 {
            return Err("case range".to_owned());
        }
        match sender.try_send(Work {
            id,
            case,
            encode: fields[2] == "c",
        }) {
            Ok(()) => (),
            Err(mpsc::TrySendError::Full(_)) => emit(&format!(
                "{{\"kind\":\"completed\",\"id\":{id},\"status\":\"rejected\"}}"
            )),
            Err(mpsc::TrySendError::Disconnected(_)) => return Err("workers exited".to_owned()),
        }
    }
    drop(sender);
    for handle in handles {
        handle.join().map_err(|_| "worker panic".to_owned())?;
    }
    emit(&format!(
        "{{\"kind\":\"drained\",\"native\":null,\"errors\":{}}}",
        ERRORS.load(Ordering::Relaxed)
    ));
    for line in lines {
        if line.map_err(|e| e.to_string())? == "snapshot" {
            emit("{\"kind\":\"idle\",\"native\":null}");
        }
    }
    if ERRORS.load(Ordering::Relaxed) != 0 {
        return Err("correctness failure".to_owned());
    }
    Ok(())
}

fn main() {
    if let Err(error) = run(&env::args().collect::<Vec<_>>()) {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
