using System.Text;
using Korda.Data;

namespace Korda.Query.Internal;

internal sealed partial class QueryCommand
{
    internal ulong Clause(uint operation, ReadOnlySpan<byte> operands)
    {
        if (operation is 0 or > 22) Unsupported();
        Mutable(operation == 7 || operation is >= 13 and <= 16 || operation is >= 19 and <= 21);
        if (operation != 19) Quota(operands.Length);
        var fields = new Fields(operands, 6);
        switch (operation)
        {
            case 1:
            case 2:
                {
                    var value = Expr(fields.Token(1));
                    if (value.Codec.Id != 4 || operation == 1 && value.Aggregate) throw new ArgumentException("The clause requires a compatible Boolean predicate.");
                    Marker(operation, operands, value.Nodes);
                    if (operation == 1) _hasWhere = true;
                    return 0;
                }
            case 3:
                {
                    var value = Expr(fields.Token(1)); Direction(fields.Number(2), fields.Number(3)); fields.Boolean(4);
                    Marker(operation, operands, value.Nodes); return 0;
                }
            case 4:
                {
                    var value = Expr(fields.Token(1));
                    if (value.Aggregate) throw new ArgumentException("An aggregate cannot be a grouping key.");
                    Marker(operation, operands, value.Nodes); _groups.Add(value.Shape); _grouped = true; return 0;
                }
            case 5:
                {
                    if (!fields.Has(1) && !fields.Has(2)) throw new ArgumentException("Empty paging operation.");
                    for (ushort id = 1; id <= 2; id++)
                        if (fields.Has(id) && fields.Token(id) > long.MaxValue) throw new ArgumentException("Paging exceeds the signed database range.");
                    Marker(operation, operands); _hasPaging = true; return 0;
                }
            case 6:
                {
                    var tokens = fields.Bytes(1, 8);
                    if (tokens.IsEmpty || tokens.Length % 8 != 0 || tokens.Length / 8 > _environment.MaxNodes) throw new ArgumentException("Invalid projection token vector.");
                    var outer = new Fields(fields.Bytes(2, 5), 13); var vector = outer.Bytes(1, 5);
                    if (vector.Length < 4 || DbWire.R32(vector, 0) != tokens.Length / 8) throw new ArgumentException("Projection field count differs.");
                    var selection = new Expression[tokens.Length / 8]; var projection = new CodecDescriptor[selection.Length];
                    var offset = 4; var nodes = 0;
                    for (var i = 0; i < selection.Length; i++)
                    {
                        if (vector.Length - offset < 4) throw new ArgumentException("Truncated projection field.");
                        var length = checked((int)DbWire.R32(vector, offset)); offset += 4;
                        if (length > vector.Length - offset) throw new ArgumentException("Projection field range.");
                        var field = new Fields(vector.Slice(offset, length), 22); offset += length;
                        var token = DbWire.R64(tokens, i * 8); var value = Expr(token);
                        var codec = _environment.Codec(field.Number(3), field.Boolean(4));
                        if (field.Number(1) != (uint)i || field.Has(5) && field.Token(5) != token
                            || !SameCodec(value.Codec, codec) || value.Bound != field.Number(3)) throw new ArgumentException("Projection expression codec or nullability differs.");
                        selection[i] = value; projection[i] = codec; nodes = checked(nodes + value.Nodes);
                    }
                    if (offset != vector.Length) throw new ArgumentException("Trailing projection bytes.");
                    Marker(operation, operands, nodes); _selection = selection; _projection = projection; return 0;
                }
            case 7:
            case 19:
                {
                    var token = fields.Token(1); var node = At(token, NodeKind.Alias); var alias = node.Alias!;
                    if (!alias.Provisional || _reserved != token) throw new ArgumentException("An active provisional JOIN alias is required.");
                    if (operation == 7)
                    {
                        var on = Expr(fields.Token(2));
                        if (on.Codec.Id != 4 || on.Aggregate) throw new ArgumentException("JOIN ON requires a scalar Boolean expression.");
                        Marker(operation, operands, checked(on.Nodes + 1 + (alias.Left ? alias.Model.Columns.Length + 2 : 0)));
                        _slots[(int)token - 1] = node with { Alias = alias with { Provisional = false } }; _hasJoin = true;
                    }
                    else
                    {
                        Append(8, operation, operands, null, 0, marker: false);
                        _slots[(int)token - 1] = node with { Valid = false };
                    }
                    for (var i = 0; i < _slots.Count; i++)
                        if (_slots[i].Expression?.Dependencies is { } dependencies && Array.IndexOf(dependencies, (uint)token) >= 0)
                            _slots[i] = _slots[i] with { Valid = false };
                    _reserved = 0; return operation == 7 ? token : 0;
                }
            case 8:
                Marker(operation, operands); return 0;
            case 9:
                {
                    _environment.Require("db.set-operations.v1"); if (fields.Number(2) > 3) Unsupported();
                    var plan = At(fields.Token(1), NodeKind.Plan).Plan!;
                    if (_projection is null || plan.Projection is null || _projection.Length != plan.Projection.Length)
                        throw new ArgumentException("Set operation selection shapes differ.");
                    for (var i = 0; i < _projection.Length; i++)
                        if (!SameCodec(_projection[i], plan.Projection[i])) throw new ArgumentException("Set operation result codec or nullability differs.");
                    Marker(operation, operands, plan.StatementNodes); _advancedMutation = true; return 0;
                }
            case 10:
                {
                    var cte = At(fields.Token(1), NodeKind.Cte).Cte!;
                    if (_cteNames.Contains(cte.Name)) throw new ArgumentException("Duplicate CTE name.");
                    Marker(operation, operands, cte.Body.StatementNodes); _cteNames.Add(cte.Name); _advancedMutation = true; return 0;
                }
            case 11:
                {
                    _environment.Require("db.window.v1"); var action = fields.Number(1);
                    if (action == 0)
                    {
                        Quota(operands.Length, expanded: 1); var result = Append(8, operation, operands, new(NodeKind.Window, WindowNodes: 1), operands.Length, 1);
                        _advancedMutation = true; return result;
                    }
                    var token = fields.Token(2); var window = At(token, NodeKind.Window); int nodes;
                    if (action is 1 or 2)
                    {
                        var value = Expr(fields.Token(3)); nodes = value.Nodes;
                        if (action == 2) Direction(fields.Number(4), fields.Number(5));
                    }
                    else if (action == 3)
                    {
                        var start = fields.Number(6); var end = fields.Number(7);
                        if (start > 1 || end < 1 || end > 2 || start > end) throw new ArgumentException("Invalid window frame endpoints.");
                        nodes = 1;
                    }
                    else { Unsupported(); return 0; }
                    Quota(operands.Length, expanded: nodes);
                    Append(8, operation, operands, null, operands.Length, nodes);
                    _slots[(int)token - 1] = window with { WindowNodes = checked(window.WindowNodes + nodes) }; return 0;
                }
            case 12:
                {
                    if (Source.SemanticProfile != 2) Unsupported(); _environment.Require("db.row-lock.v1");
                    if (fields.Number(1) > 3 || fields.Number(2) > 2) Unsupported();
                    Marker(operation, operands); return 0;
                }
            case 13:
                return Aggregate(operands, fields);
            case 14:
                {
                    var plan = At(fields.Token(1), NodeKind.Plan).Plan!;
                    if (plan.Projection is null) throw new ArgumentException("A subquery projection is required.");
                    var mode = fields.Number(2); var nullable = false; uint[]? dependencies = null;
                    var depth = plan.Depth + 1; var nodes = checked(plan.StatementNodes + 1); var aggregate = false;
                    if (mode == 1)
                    {
                        var lhs = Expr(fields.Token(3));
                        if (plan.Projection.Length != 1 || !plan.Projection[0].SameScalar(lhs.Codec)) throw new ArgumentException("IN subquery scalar shape differs.");
                        nullable = lhs.Codec.Nullable || plan.Projection[0].Nullable; dependencies = lhs.Dependencies;
                        depth = Math.Max(lhs.Depth, plan.Depth) + 1; aggregate = lhs.Aggregate; nodes = checked(nodes + lhs.Nodes);
                    }
                    else if (mode != 0) Unsupported();
                    return Emit(8, operation, operands, new(Boolean(nullable), 4, depth, nodes, aggregate, false, dependencies, default), operands.Length);
                }
            case 15:
                {
                    var condition = Expr(fields.Token(1)); var yes = Expr(fields.Token(2)); var no = Expr(fields.Token(3));
                    if (condition.Codec.Id != 4 || !yes.Codec.SameScalar(no.Codec) || yes.Bound != no.Bound) throw new ArgumentException("CASE operand types differ.");
                    var codec = yes.Codec with { Nullable = yes.Codec.Nullable || no.Codec.Nullable };
                    return Emit(8, operation, operands, new(codec, yes.Bound, Math.Max(condition.Depth, Math.Max(yes.Depth, no.Depth)) + 1,
                        checked(condition.Nodes + yes.Nodes + no.Nodes + 1), condition.Aggregate || yes.Aggregate || no.Aggregate, false,
                        Dependencies(condition, yes, no), 0), operands.Length, shape: Shape(new(8, 15, condition.Shape, yes.Shape, no.Shape)));
                }
            case 16:
                return Text(operands, fields);
            case 17:
                {
                    var id = fields.Number(1); var column = _model.Column(id); var value = Expr(fields.Token(2));
                    if (column.KeyOrdinal is not null || column.Options.IsGenerated || _assigned.Contains(id)
                        || !column.Codec.SameScalar(value.Codec) || !column.Codec.Nullable && value.Codec.Nullable
                        || value.Aggregate || value.Dependencies is { Length: > 0 }) throw new ArgumentException("Invalid root assignment.");
                    Marker(operation, operands, value.Nodes); _assigned.Add(id); return 0;
                }
            case 18:
                {
                    _environment.Require("db.relationships.explicit.v1");
                    var model = At(fields.Token(1), NodeKind.Model).Model!; var nameBytes = fields.Bytes(3, 4); var name = Utf8.GetString(nameBytes);
                    if (nameBytes.Length is 0 or > 128 || name.EnumerateRunes().Any(rune => rune.Value < 32 || rune.Value is >= 127 and <= 159))
                        throw new ArgumentException("Invalid JOIN alias name.");
                    if (fields.Number(2) != model.TableId || fields.Number(4) > 1) throw new ArgumentException("JOIN table or kind differs.");
                    if (_slots.Any(node => node.Valid && node.Alias?.Name == name)) throw new ArgumentException("Duplicate JOIN alias.");
                    var result = Append(8, operation, operands, new(NodeKind.Alias, Alias: new(model, name, fields.Boolean(4), true)), operands.Length);
                    _reserved = result; return result;
                }
            case 20:
                {
                    var token = fields.Token(1); var alias = At(token, NodeKind.Alias).Alias!;
                    if (!alias.Left || alias.Provisional) throw new ArgumentException("JOIN MATCH requires a committed left alias.");
                    return Emit(8, operation, operands, new(Boolean(), 4, 1, 2, false, false, [checked((uint)token)], 0), operands.Length, shape: Shape(new(8, 20, Text: alias.Name)));
                }
            case 21:
                {
                    _environment.Require("db.window.v1"); var value = Expr(fields.Token(1));
                    if (!value.Windowable) throw new ArgumentException("OVER requires an aggregate or window expression.");
                    var window = At(fields.Token(2), NodeKind.Window);
                    return Emit(8, operation, operands, value with
                    {
                        Nodes = checked(value.Nodes + window.WindowNodes + 1),
                        Depth = value.Depth + 1,
                        Windowable = false,
                        WindowCast = false,
                        Shape = default
                    }, operands.Length);
                }
            case 22:
                fields.Duration(1); Marker(operation, operands); return 0;
            default:
                Unsupported(); return 0;
        }
    }

    private void Marker(uint operation, ReadOnlySpan<byte> operands, int statement = 0)
    {
        Quota(operands.Length, expanded: statement);
        Append(8, operation, operands, null, operands.Length, statement, statement);
    }

    private ulong Aggregate(ReadOnlySpan<byte> operands, Fields fields)
    {
        var function = fields.Number(1); var distinct = fields.Boolean(3);
        if (function > 7) Unsupported();
        if (fields.Has(4) || function >= 6) _environment.Require("db.window.v1");
        var input = fields.Has(2) ? Expr(fields.Token(2)) : null;
        if ((function == 0 || function >= 6) && (input is not null || distinct) || function is >= 1 and <= 5 && input is null)
            throw new ArgumentException("Aggregate operand set differs.");
        var codec = _environment.Codec(fields.Number(5), function is >= 2 and <= 5);
        if ((function <= 1 || function >= 6) && codec.Id != 3) throw new ArgumentException("Count and rank require Int64 results.");
        if (input is not null)
        {
            if (input.Aggregate) throw new ArgumentException("A nested aggregate requires a subquery.");
            if (function is 2 or 3)
            {
                var id = input.Codec.Id; if (!Numeric(id)) Unsupported();
                var expected = id <= 3 ? (function == 2 ? 3u : 11u) : id is 11 or 12 ? 11u : id;
                if (codec.Id != expected || expected is 13 or 15 && !codec.SameScalar(input.Codec)) throw new ArgumentException("Aggregate widening or configuration differs.");
            }
            if (function is 4 or 5 && !codec.SameScalar(input.Codec)) throw new ArgumentException("Min/max result codec differs.");
        }
        var nodes = 1 + (input?.Nodes ?? 0); var hasWindow = fields.Has(4);
        if (hasWindow) nodes = checked(nodes + At(fields.Token(4), NodeKind.Window).WindowNodes);
        var cast = Source.SemanticProfile == 2 && input is not null &&
            (function == 2 && (input.Codec.Id is >= 1 and <= 3 or 11 or 12 or 15) || function == 3 && input.Codec.Id is >= 1 and <= 3 or 11 or 12);
        if (cast) { if (!hasWindow) nodes = checked(nodes * 2); nodes++; }
        return Emit(8, 13, operands, new(codec, fields.Number(5), (input?.Depth ?? 0) + 1, nodes, true, !hasWindow,
            input?.Dependencies, default, cast && !hasWindow), operands.Length);
    }

    private ulong Text(ReadOnlySpan<byte> operands, Fields fields)
    {
        var operation = fields.Number(1); if (operation > 4) Unsupported();
        var input = Expr(fields.Token(2)); if (input.Codec.Id != 5) throw new ArgumentException("A text input codec is required.");
        var escape = fields.Number(4);
        if (escape == 0 || escape > 0x10ffff || escape is >= 0xd800 and <= 0xdfff) throw new ArgumentException("LIKE escape must be a Unicode scalar.");
        QueryOrigin? origin = null; var literalBytes = 0; var codec = input.Codec; var bound = input.Bound; ShapeIdentity shape;
        if (operation == 4)
        {
            var name = Utf8.GetString(fields.Bytes(3, 4));
            if (Source.SemanticProfile == 1 ? name is not ("BINARY" or "NOCASE" or "RTRIM") : name is not ("C" or "POSIX" or "default")) Unsupported();
            shape = Shape(new(8, 16, input.Shape, Text: name));
        }
        else
        {
            var bytes = fields.Bytes(3, 6); var value = Values(bytes, 1);
            if (value.Codec.Id != 5) throw new ArgumentException("A text argument codec is required.");
            if (DbWire.R16(bytes, 114) == 0)
            {
                var start = checked((int)DbWire.R64(bytes, 120)); var length = checked((int)DbWire.R64(bytes, 128));
                var text = Utf8.GetString(bytes.Slice(start, length));
                literalBytes = operation is 0 or 2 ? 1 : 0;
                var escapeLength = new Rune((int)escape).Utf8SequenceLength;
                foreach (var rune in text.EnumerateRunes())
                {
                    if (operation != 3 && (rune.Value == escape || rune.Value is '%' or '_')) literalBytes = checked(literalBytes + escapeLength);
                    literalBytes = checked(literalBytes + rune.Utf8SequenceLength);
                }
                if (operation is 0 or 1) literalBytes++;
            }
            Quota(operands.Length, input.Depth + 1, checked(input.Nodes + 3), literalBytes);
            codec = Boolean(input.Codec.Nullable || value.Codec.Nullable); bound = 4; origin = _environment.Origin(); shape = default;
        }
        var result = Emit(8, 16, operands, input with
        {
            Codec = codec,
            Bound = bound,
            Depth = input.Depth + 1,
            Nodes = checked(input.Nodes + (operation == 4 ? 2 : 3)),
            Windowable = false,
            WindowCast = false,
            Shape = 0
        }, operands.Length, origin, shape);
        if (origin is not null) _literals.Add(origin.Identity, literalBytes); return result;
    }

    private static bool SameCodec(CodecDescriptor left, CodecDescriptor right) => left.SameScalar(right) && left.Nullable == right.Nullable;
    private static void Direction(uint order, uint nulls)
    { if (order > 1 || nulls > 1) throw new ArgumentException("Invalid ordering direction or NULL placement."); }

    private readonly ref struct Fields
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly int _count;
        internal Fields(ReadOnlySpan<byte> bytes, ushort kind)
        {
            if (bytes.Length < 16 || DbWire.R16(bytes, 0) != 1 || DbWire.R16(bytes, 2) != kind || DbWire.R32(bytes, 4) != bytes.Length
                || DbWire.R32(bytes, 12) != 0 || DbWire.R32(bytes, 8) > 4096) throw new ArgumentException("Invalid bounded query operand header.");
            _count = checked((int)DbWire.R32(bytes, 8)); var start = checked(16 + _count * 16);
            if (start > bytes.Length) throw new ArgumentException("Truncated query operand table.");
            ushort previous = 0;
            for (var i = 0; i < _count; i++)
            {
                var at = 16 + i * 16; var id = DbWire.R16(bytes, at); var length = DbWire.R32(bytes, at + 4); var offset = DbWire.R64(bytes, at + 8);
                if (id <= previous || offset < (ulong)start || offset > (ulong)bytes.Length || length > (ulong)bytes.Length - offset)
                    throw new ArgumentException("Invalid query operand field range.");
                previous = id;
            }
            _bytes = bytes;
        }
        private int Find(ushort id)
        {
            for (var i = 0; i < _count; i++) { var offset = 16 + i * 16; if (DbWire.R16(_bytes, offset) == id) return offset; }
            return -1;
        }
        internal bool Has(ushort id) => Find(id) >= 0;
        internal ReadOnlySpan<byte> Bytes(ushort id, ushort type)
        {
            var at = Find(id); if (at < 0 || DbWire.R16(_bytes, at + 2) != type) throw new ArgumentException("Missing or incompatible query operand field.");
            return _bytes.Slice(checked((int)DbWire.R64(_bytes, at + 8)), checked((int)DbWire.R32(_bytes, at + 4)));
        }
        internal uint Number(ushort id) { var bytes = Bytes(id, 1); if (bytes.Length != 4) throw new ArgumentException("Invalid UInt32 operand width."); return DbWire.R32(bytes, 0); }
        internal bool Boolean(ushort id) { var value = Number(id); if (value > 1) throw new ArgumentException("Invalid Boolean operand."); return value != 0; }
        internal ulong Token(ushort id) { var bytes = Bytes(id, 2); if (bytes.Length != 8) throw new ArgumentException("Invalid token operand width."); return DbWire.R64(bytes, 0); }
        internal ulong Duration(ushort id) { var bytes = Bytes(id, 3); if (bytes.Length != 8) throw new ArgumentException("Invalid duration operand width."); return DbWire.R64(bytes, 0); }
    }
}
