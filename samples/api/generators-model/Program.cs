using NativeWeave.Generators.Model;
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;

if (args.Length != 2) throw new ArgumentException("Sample <validated-sidecar> <native-fixture-sha256>");
// Maintainer build step: no Cargo, network, database or native loading occurs here.
var model = BindingReader.Read(File.ReadAllBytes(args[0]), args[0]);
var staticallySupplied = BindingReader.Read(model.GetCanonicalBytes(), "static-descriptor");
model.VerifyParity(staticallySupplied);

// The existing public metadata API is reused; this sample does not invent a runtime facade.
var manifest = new EnginePackageManifest("model.fixture", "NativeWeave.Model.Fixture", "0.1.0",
    new AbiVersion(1, 0), "nw_model_get_abi_v1", EngineThreadingModel.Synchronous,
    [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_model_fixture.so", args[1], "x86-64")],
    [new EngineCapabilityDefinition(new EngineCapability("model.extent.v1"), 0)]);
var descriptor = EnginePackage.CreateDescriptor(manifest, typeof(Program).Assembly);
Console.WriteLine($"{descriptor.EngineId}: {model.Symbols.Length} exact exports; description sha256={model.Digest}");
