//! Maintainer-only inventory extraction. Every explicitly exported function is retained,
//! including types a downstream raw-binding generator may silently omit.
use quote::ToTokens;
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{env, fs};
use syn::{Attribute, Item, Meta};

fn export_name(attrs: &[Attribute], fallback: &str) -> Result<Option<String>, String> {
    let mut result = None;
    for attribute in attrs {
        let meta = if attribute.path().is_ident("unsafe") {
            attribute.parse_args::<Meta>().map_err(|e| e.to_string())?
        } else {
            attribute.meta.clone()
        };
        let candidate = match meta {
            Meta::Path(path) if path.is_ident("no_mangle") => Some(fallback.to_owned()),
            Meta::NameValue(value) if value.path.is_ident("export_name") => {
                if let syn::Expr::Lit(syn::ExprLit {
                    lit: syn::Lit::Str(name),
                    ..
                }) = value.value
                {
                    Some(name.value())
                } else {
                    return Err("export_name must be a literal C symbol".into());
                }
            }
            _ => None,
        };
        if let Some(name) = candidate
            && result.replace(name).is_some()
        {
            return Err("duplicate export attributes".into());
        }
    }
    Ok(result)
}

fn walk(items: &[Item], file: &str, exports: &mut Vec<serde_json::Value>) -> Result<(), String> {
    for item in items {
        match item {
            Item::Fn(function) => {
                let Some(symbol) = export_name(&function.attrs, &function.sig.ident.to_string())?
                else {
                    continue;
                };
                // A target-conditioned source must be normalized upstream first. Silently guessing
                // an inactive export would weaken the linked-inventory equality gate.
                if function
                    .attrs
                    .iter()
                    .any(|a| a.path().is_ident("cfg") || a.path().is_ident("cfg_attr"))
                {
                    return Err(format!(
                        "{file}:{}: target-condition the export inventory before extraction",
                        function.sig.ident.span().start().line
                    ));
                }
                let mut parameters = Vec::new();
                for argument in &function.sig.inputs {
                    match argument {
                        syn::FnArg::Typed(parameter) => {
                            parameters.push(parameter.ty.to_token_stream().to_string())
                        }
                        syn::FnArg::Receiver(_) => {
                            return Err("an exported function cannot have self".into());
                        }
                    }
                }
                let return_type = match &function.sig.output {
                    syn::ReturnType::Default => "()".into(),
                    syn::ReturnType::Type(_, ty) => ty.to_token_stream().to_string(),
                };
                let abi = function
                    .sig
                    .abi
                    .as_ref()
                    .and_then(|a| a.name.as_ref())
                    .map(|n| n.value())
                    .unwrap_or_default();
                let location = function.sig.ident.span().start();
                exports.push(json!({"symbol":symbol,"calling_convention":abi,"return_type":return_type,
                    "generic":!function.sig.generics.params.is_empty() || function.sig.variadic.is_some() || function.sig.asyncness.is_some(),
                    "parameters":parameters,"file":file,"line":location.line,"column":location.column+1}));
            }
            Item::Mod(module) => {
                if let Some((_, children)) = &module.content {
                    walk(children, file, exports)?;
                }
                // External modules require their own explicit extraction input; never recursively do I/O.
            }
            Item::Static(value)
                if export_name(&value.attrs, &value.ident.to_string())?.is_some() =>
            {
                return Err(
                    "exported global storage is outside the function-only ABI profile".into(),
                );
            }
            _ => {}
        }
    }
    Ok(())
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args().collect();
    if args.len() != 3 {
        return Err(
            "usage: nativeweave-binding-inventory <explicit-rust-file> <output-json>".into(),
        );
    }
    let input = fs::read(&args[1])?;
    if input.len() > 1024 * 1024 {
        return Err("inventory input exceeds 1 MiB".into());
    }
    let syntax = syn::parse_file(std::str::from_utf8(&input)?)?;
    let mut exports = Vec::new();
    walk(&syntax.items, &args[1], &mut exports)?;
    exports.sort_by(|a, b| a["symbol"].as_str().cmp(&b["symbol"].as_str()));
    let digest = format!("{:x}", Sha256::digest(&input));
    let result = json!({"schema_version":1,"adapter_sha256":digest,"tool":"nativeweave-binding-inventory",
        "tool_version":env!("CARGO_PKG_VERSION"),"parser":"syn-2.0.119","exports":exports});
    fs::write(&args[2], serde_json::to_vec_pretty(&result)?)?;
    Ok(())
}
