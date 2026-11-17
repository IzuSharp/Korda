//! Behavioral baseline invoking pinned upstream miniz_oxide directly, without FFI/Core.
use std::{env, fs};
fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args().collect();
    if args.len() != 4 {
        return Err("usage: direct encode|decode input output".into());
    }
    let input = fs::read(&args[2])?;
    let output = match args[1].as_str() {
        "encode" => miniz_oxide::deflate::compress_to_vec_zlib(&input, 6),
        "decode" => {
            miniz_oxide::inflate::decompress_to_vec_zlib_with_limit(&input, 16 * 1024 * 1024)
                .map_err(|e| e.to_string())?
        }
        _ => return Err("unknown operation".into()),
    };
    fs::write(&args[3], output)?;
    Ok(())
}
