using Korda.Compatibility.Conformance;

#if MIGRATED
int answer = Contract.Sum(35, 7);
Func<int, int, int> add = Contract.Sum;
int named = Contract.Sum(value: 35, offset: 7);
#else
int answer = Contract.Add(35);
Func<int, int, int> add = Contract.Add;
int named = Contract.Add(value: 35, offset: 7);
#endif
Func<string?, string?> normalize = Contract.Normalize;
Func<int, int> genericLambda = value => Contract.Echo(value);
if (answer != 42 || named != 42 || add(35, 7) != 42 || normalize(null) is not null || genericLambda(42) != 42
    || Contract.Normalize(null) is not null || Contract.Echo(42) != 42 || Contract.Lookup(null) != 0 || Contract.Lookup(default) != 0)
    throw new InvalidOperationException("Source/binary/default/behavior compatibility changed.");
Console.WriteLine("Source/binary/default/nullability/generic consumer passed.");
