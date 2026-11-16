fn main() {
    let args: Vec<_> = std::env::args().collect();
    csbindgen::Builder::default()
        .input_extern_file(&args[1])
        .csharp_namespace("NativeWeave.Generators.Model.Fixture")
        .csharp_class_name("RawImports")
        .csharp_dll_name("nativeweave_model_fixture")
        .generate_csharp_file(&args[2])
        .expect("raw extraction failed");
}
