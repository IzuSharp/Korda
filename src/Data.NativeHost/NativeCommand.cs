using Korda.Interop.Abi;
using Korda.Query.Internal;

namespace Korda.Database;

internal delegate T CommandResultReader<T>(SourceBinding binding, ref NativeCommandResult result);

internal static unsafe class NativeCommand
{
    internal static CommandCompletionCalls Calls(SourceBinding binding) => binding.Completions
        ?? throw new NotSupportedException("The native command completion extension is unavailable.");

    internal static uint Request(NativeCommandKind kind, DatabaseOwner target, DatabaseOwner? input,
        ReadOnlyMemory<byte> payload, uint action, in NativePullCallbacks callbacks,
        ref nint command, ref NwErrorInfoV1 error)
    {
        var binding = target.Source;
        if (input is not null) BindingChecks.SameSource(binding, input);
        using var targetLease = target.Borrow(target.Kind);
        using var inputLease = input is null ? default : input.Borrow(input.Kind);
        fixed (byte* bytes = payload.Span)
        fixed (NativePullCallbacks* callbackPointer = &callbacks)
        fixed (nint* output = &command)
        fixed (NwErrorInfoV1* failure = &error)
        {
            NativeCommandRequest request = new()
            {
                StructSize = (uint)sizeof(NativeCommandRequest), Kind = kind,
                Target = targetLease.Pointer, Input = inputLease.Pointer,
                Payload = BindingChecks.View(bytes, payload.Length), Action = action
            };
            return Calls(binding).Request(&request, callbackPointer, output, failure);
        }
    }

    internal static uint Take(SourceBinding binding, nint command, uint kind,
        ref NativeCommandResult result, ref NwErrorInfoV1 error)
    {
        fixed (NativeCommandResult* output = &result)
        fixed (NwErrorInfoV1* failure = &error)
            return Calls(binding).Take(command, kind, output, failure);
    }

    internal static void Validate(in NativeCommandResult result, uint kind)
    {
        if (result.StructSize != sizeof(NativeCommandResult) || result.Kind != kind
            || (kind is 3 or 4 ? result.Owner == 0 || result.Buffer != 0 || result.Count != 0
                : kind == 100 ? result.Owner != 0 || result.Buffer != 0
                : kind == 101 ? result.Buffer == 0 || result.Owner != 0 || result.Count != 0 : true))
            throw new InvalidOperationException("Native command returned an incompatible result record.");
    }

    internal static long Count(SourceBinding binding, ref NativeCommandResult result) => result.Count;

    internal static DatabaseOwner Owner(SourceBinding binding, ref NativeCommandResult result)
    {
        var owner = new DatabaseOwner(binding, result.Kind == 3 ? DatabaseOwnerKind.Transaction : DatabaseOwnerKind.Savepoint, result.Owner);
        result.Owner = 0;
        return owner;
    }

    internal static TransactionSnapshot Snapshot(SourceBinding binding, ref NativeCommandResult result)
    {
        using var buffer = new NativeBuffer(binding, result.Buffer);
        result.Buffer = 0;
        return buffer.ReadSnapshot();
    }

    internal static Exception Failure(DataSource source, nint command, uint status,
        in NwErrorInfoV1 error, CancellationToken token, out bool detailsTaken)
    {
        nint pointer = 0; var detail = BindingChecks.Error();
        var taken = Calls(source.Binding).TakeError(command, &pointer, &detail);
        detailsTaken = taken == NativeStatus.Ok;
        if (taken != NativeStatus.Ok) return HostFailure.Create(source.Binding, status, error, token: token);
        return HostFailure.ReadDetails(source.Binding, status, error, token, pointer, source.TestAfterErrorTake,
            "Native command cleanup also failed; the connection cannot be reused.");
    }
}
