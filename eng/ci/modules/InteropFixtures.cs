using System.Text.Json;

namespace NativeWeave.CI;

internal static class InteropFixtures
{
    public static void Run(BuildContext ctx, bool corpus)
    {
        string target = corpus ? "interop-corpus" : "abi-bootstrap";
        string build = Path.Combine(ctx.Root, "artifacts", "fixture-tests", target);
        ctx.Run("cmake", "-S", "eng/ci/fixtures", "-B", build, "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_C_COMPILER=clang", "-DCMAKE_CXX_COMPILER=clang++", "-DCHECK_INTEROP_CORPUS=" + (corpus ? "ON" : "OFF"));
        ctx.Run("cmake", "--build", build, "--target", target);
        string executable = Path.Combine(build, target);
        if (!corpus)
        {
            ctx.Run(executable);
            return;
        }
        using var seeds = JsonDocument.Parse(File.ReadAllText(Path.Combine(ctx.Root, "tests/Verification.Interop/corpus/seeds.json")));
        var inputs = seeds.RootElement.GetProperty("cases").EnumerateArray().Select((item, i) =>
        {
            string file = Path.Combine(build, $"seed-{i}.bin");
            File.WriteAllBytes(file, Convert.FromHexString(item.GetProperty("hex").GetString()!));
            return file;
        }).ToArray();
        if (inputs.Length == 0) throw new InvalidDataException("The interop corpus is empty.");
        ctx.Run(executable, inputs);
    }
}
